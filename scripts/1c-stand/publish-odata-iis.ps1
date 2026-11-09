#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Публикует файловую базу 1С в IIS — только стандартный интерфейс OData, без веб-клиента.

.DESCRIPTION
  Стенд для разработки интеграции с 1С (docs/DEV_NOTES.md, «Стенд 1С: OData и раннер»).
  Идемпотентен: повторный запуск приводит IIS к тому же состоянию, ничего не задваивая.

  Что делает:
    1. Проверяет, что у платформы есть модуль расширения веб-сервера (wsisapi.dll). Его нет —
       отказ с инструкцией: компонент ставится из дистрибутива платформы, скрипт его не скачивает.
    2. Включает компоненты IIS: ISAPI-расширения и фильтры, консоль управления.
    3. Заводит свой пул (без .NET, 64 бита) и свой сайт на 127.0.0.1:<порт> — отдельно от
       Default Web Site, чтобы не спорить за 80/443 с nginx дев-стенда.
    4. Кладёт default.vrd: веб-клиент и тонкий клиент по HTTP ВЫКЛЮЧЕНЫ (enable="false"),
       включён только standardOdata.
    5. Даёт удостоверению пула права: изменение — на каталог базы (файловой базе нужны
       файлы блокировок), чтение — на платформу и лицензии.

  Сайт слушает только петлю: база — копия рабочей, но данные в ней настоящие, наружу не выпускаем.

.EXAMPLE
  sudo pwsh -NoProfile -File scripts/1c-stand/publish-odata-iis.ps1
.EXAMPLE
  sudo pwsh -NoProfile -File scripts/1c-stand/publish-odata-iis.ps1 -InfobasePath 'E:\Accounting\BHS.CRG' -Port 8090
#>
[CmdletBinding()]
param(
    [string]$InfobasePath = 'E:\Accounting\BHS.CRG',
    [string]$PlatformBin  = 'C:\Program Files\1cv8\8.5.1.1529\bin',
    [string]$SiteName     = '1C',
    [string]$PoolName     = '1C-BHS-CRG',
    [string]$AppName      = 'bhs-crg',
    [string]$SiteRoot     = 'C:\inetpub\1c',
    [string]$BindAddress  = '127.0.0.1',
    [int]$Port            = 8090
)

$ErrorActionPreference = 'Stop'
$appcmd = Join-Path $env:windir 'system32\inetsrv\appcmd.exe'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-AppCmd {
    # appcmd отвечает кодом, а не исключением; «уже существует» (183) считаем успехом.
    # Ключи — только через «/»: -section:x PowerShell режет на два аргумента («-section:» и «x»),
    # и appcmd принимает путь раздела за имя сайта.
    param([Parameter(ValueFromRemainingArguments)][string[]]$AppCmdArgs)
    $out = & $appcmd @AppCmdArgs 2>&1
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 183) {
        throw "appcmd $($AppCmdArgs -join ' ') -> $LASTEXITCODE`n$out"
    }
    $out
}

# --- 1. Платформа ------------------------------------------------------------------------------
Step 'Проверяю модуль расширения веб-сервера 1С'
$isapi = Join-Path $PlatformBin 'wsisapi.dll'
if (-not (Test-Path $isapi)) {
    throw @"
Нет $isapi — у платформы не установлен «Модуль расширения веб-сервера».
Поставьте его из дистрибутива ТОЙ ЖЕ версии платформы ($(Split-Path (Split-Path $PlatformBin) -Leaf)):
  Параметры → Приложения → «1С:Предприятие 8 (x86-64)» → Изменить → Изменить →
  отметить «Модули расширения веб-сервера» → Далее.
Установщику нужен исходный дистрибутив (releases.1c.ru, «Технологическая платформа, 64-bit, Windows»).
Потом запустите этот скрипт ещё раз.
"@
}
if (-not (Test-Path (Join-Path $InfobasePath '1Cv8.1CD'))) {
    throw "В $InfobasePath нет 1Cv8.1CD — это не каталог файловой базы."
}

# --- 2. IIS ------------------------------------------------------------------------------------
Step 'Включаю компоненты IIS (ISAPI)'
$features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-ISAPIExtensions', 'IIS-ISAPIFilter',
            'IIS-ManagementConsole', 'IIS-RequestFiltering', 'IIS-HttpErrors'
$missing = $features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_).State -ne 'Enabled' }
if ($missing) {
    $res = Enable-WindowsOptionalFeature -Online -All -NoRestart -FeatureName $missing
    # Без перезагрузки включённое может не заработать, а скрипт дошёл бы до «Готово» — и публикация
    # отвечала бы 404/500 без объяснения. Говорим сразу.
    if (@($res | Where-Object RestartNeeded).Count) {
        Write-Warning "Windows просит перезагрузку для компонентов IIS ($($missing -join ', ')). Перезагрузитесь и запустите скрипт ещё раз — до этого публикация может не отвечать."
    }
}

Step "Разрешаю wsisapi.dll в ограничениях ISAPI"
$restr = (& $appcmd list config /section:system.webServer/security/isapiCgiRestriction) -join "`n"
if ($restr -notmatch [regex]::Escape($isapi)) {
    Invoke-AppCmd set config /section:system.webServer/security/isapiCgiRestriction `
        "/+[path='$isapi',allowed='True',description='1C:Enterprise $(Split-Path (Split-Path $PlatformBin) -Leaf)']" `
        /commit:apphost | Out-Null
}

# --- 3. Пул и сайт -----------------------------------------------------------------------------
# Наличие — по полному списку имён: «не найдено» appcmd печатает в тот же вывод, что и находку,
# и проверка по пустоте вывода приняла бы отказ за существующий объект.
function Test-IisObject($kind, $field, $name) { @(& $appcmd list $kind /text:$field) -contains $name }

Step "Пул $PoolName (без .NET, 64 бита, с профилем)"
if (-not (Test-IisObject apppool name $PoolName)) { Invoke-AppCmd add apppool /name:$PoolName | Out-Null }
Invoke-AppCmd set apppool /apppool.name:$PoolName `
    /managedRuntimeVersion: /enable32BitAppOnWin64:false `
    /processModel.identityType:ApplicationPoolIdentity /processModel.loadUserProfile:true | Out-Null

Step "Сайт $SiteName на ${BindAddress}:$Port"
$appDir = Join-Path $SiteRoot $AppName
New-Item -ItemType Directory -Force $appDir | Out-Null
if (-not (Test-IisObject site name $SiteName)) {
    Invoke-AppCmd add site /name:$SiteName "/physicalPath:$SiteRoot" "/bindings:http/${BindAddress}:${Port}:" | Out-Null
} else {
    Invoke-AppCmd set site /site.name:$SiteName "/bindings:http/${BindAddress}:${Port}:" | Out-Null
}
Invoke-AppCmd set site /site.name:$SiteName "/[path='/'].applicationPool:$PoolName" | Out-Null

$appPath = "$SiteName/$AppName"
if (-not (Test-IisObject app app.name $appPath)) {
    Invoke-AppCmd add app /site.name:$SiteName /path:/$AppName "/physicalPath:$appDir" | Out-Null
}
Invoke-AppCmd set app $appPath /applicationPool:$PoolName | Out-Null

Step 'Обработчик 1С на приложении'
# Раздел handlers по умолчанию заперт на уровне сервера — пишем в applicationHost (commit:apphost),
# а не в web.config приложения, иначе IIS ответит 500.19.
# Вывод appcmd — массив строк, а `-notmatch` на массиве отбирает строки, а не отвечает «да/нет»:
# склеиваем, иначе повторный запуск добавлял бы запись второй раз и падал на дубле.
$handlers = (& $appcmd list config $appPath /section:system.webServer/handlers) -join "`n"
if ($handlers -notmatch '1C Web-service Extension') {
    Invoke-AppCmd set config $appPath /section:system.webServer/handlers `
        "/+[name='1C Web-service Extension',path='*',verb='*',modules='IsapiModule',scriptProcessor='$isapi',resourceType='Unspecified',requireAccess='None',preCondition='bitness64']" `
        /commit:apphost | Out-Null
}
Invoke-AppCmd set config $appPath /section:system.webServer/handlers "/accessPolicy:Read, Execute, Script" /commit:apphost | Out-Null

# --- 4. Публикация -----------------------------------------------------------------------------
Step 'default.vrd: только OData'
$ib = 'File=&quot;' + [Security.SecurityElement]::Escape($InfobasePath) + '&quot;;'
$vrd = @"
<?xml version="1.0" encoding="UTF-8"?>
<point xmlns="http://v8.1c.ru/8.2/virtual-resource-system"
		xmlns:xs="http://www.w3.org/2001/XMLSchema"
		xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
		base="/$AppName"
		ib="$ib"
		enable="false">
	<standardOdata enable="true"
			reuseSessions="autouse"
			sessionMaxAge="20"
			poolSize="10"
			poolTimeout="5"/>
	<httpServices publishByDefault="false"/>
	<ws publishExtensionsByDefault="false" pointEnableCommon="false"/>
</point>
"@
# enable="false" закрывает веб-клиент, но HTTP-сервисы конфигурации публикуются по умолчанию
# (проверено: hs/ отвечал 401 — дверь есть). Закрыты явно: от стенда нужен только OData.
# UTF-8 без BOM: модуль 1С читает vrd как XML, BOM ему не нужен.
[IO.File]::WriteAllText((Join-Path $SiteRoot "$AppName\default.vrd"), $vrd, [Text.UTF8Encoding]::new($false))

# --- 5. Права ----------------------------------------------------------------------------------
Step "Права удостоверению IIS AppPool\$PoolName"
$who = "IIS AppPool\$PoolName"
& icacls $InfobasePath /grant "${who}:(OI)(CI)M" /Q | Out-Null
& icacls $PlatformBin /grant "${who}:(OI)(CI)RX" /Q | Out-Null
& icacls (Join-Path $SiteRoot $AppName) /grant "${who}:(OI)(CI)RX" /Q | Out-Null
$lic = Join-Path $env:ProgramData '1C\licenses'
if (Test-Path $lic) { & icacls $lic /grant "${who}:(OI)(CI)RX" /Q | Out-Null }
else { Write-Warning "Нет ${lic}: если лицензия программная, она лежит не там — дайте пулу чтение на её каталог." }

Step 'Перезапуск пула'
Invoke-AppCmd recycle apppool /apppool.name:$PoolName | Out-Null
Invoke-AppCmd start site /site.name:$SiteName | Out-Null

$url = "http://${BindAddress}:$Port/$AppName/odata/standard.odata/"
Write-Host ''
Write-Host "Готово. Адрес OData: $url" -ForegroundColor Green
Write-Host 'Проверка (спросит пользователя 1С):  pwsh -NoProfile -File scripts/1c-stand/check-odata.ps1'
Write-Host 'Пустой $metadata — состав интерфейса OData в самой базе не задан (см. DEV_NOTES).'
