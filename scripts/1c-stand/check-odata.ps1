<#
.SYNOPSIS
  Проверяет живую публикацию OData базы 1С: ответ, состав интерфейса, чтение одной записи.

.DESCRIPTION
  Только чтение: запросы GET, ни одной записи в базу — проверка стенда не должна менять данные,
  которые тесты интеграции ждут увидеть. База стенда — копия рабочей (DEV_NOTES).

  Учётные данные — из ONEC_ODATA_USER / ONEC_ODATA_PASSWORD (так их даёт workflow из секретов
  окружения «1c»), иначе спрашивает окно Get-Credential.

  Отказ — это отказ: код выхода 1 и названная причина. Пустой состав интерфейса — тоже отказ, а не
  «0 наборов, всё хорошо»: интеграции с пустым $metadata делать нечего.

.EXAMPLE
  pwsh -NoProfile -File scripts/1c-stand/check-odata.ps1
#>
[CmdletBinding()]
param(
    [string]$Url = $(if ($env:ONEC_ODATA_URL) { $env:ONEC_ODATA_URL } else { 'http://127.0.0.1:8090/bhs-crg/odata/standard.odata/' })
)

$ErrorActionPreference = 'Stop'
if (-not $Url.EndsWith('/')) { $Url += '/' }

if ($env:ONEC_ODATA_USER) {
    $pw = ConvertTo-SecureString ($env:ONEC_ODATA_PASSWORD ?? '') -AsPlainText -Force
    $cred = [pscredential]::new($env:ONEC_ODATA_USER, $pw)
} elseif ($env:GITHUB_ACTIONS -eq 'true') {
    # Окна ввода у службы раннера нет: Get-Credential здесь висел бы до таймаута работы.
    Write-Host 'ОТКАЗ: нет ONEC_ODATA_USER — секреты окружения «1c» не заданы или не дошли до задания.' -ForegroundColor Red
    exit 1
} else {
    $cred = Get-Credential -Message "Пользователь 1С для $Url"
}

function Get-OData($relative) {
    # -SkipHttpErrorCheck: код ответа разбираем сами, чтобы назвать причину, а не стек.
    try {
        $r = Invoke-WebRequest -Uri ($Url + $relative) -Credential $cred -Authentication Basic `
            -AllowUnencryptedAuthentication -SkipHttpErrorCheck -TimeoutSec 60
    } catch {
        Write-Host "ОТКАЗ: нет ответа от $Url — сайт IIS не запущен или порт другой.`n$($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    if ($r.StatusCode -ne 200) {
        $hint = switch ($r.StatusCode) {
            401     { 'пользователь или пароль 1С не подходят (или у пользователя нет права на OData)' }
            404     { 'публикации нет по этому адресу — проверьте сайт IIS и default.vrd' }
            500     { 'модуль 1С упал: часто это права пула на каталог базы или лицензия' }
            default { 'см. тело ответа' }
        }
        $body = ($r.Content | Out-String).Trim()
        if ($body.Length -gt 600) { $body = $body.Substring(0, 600) + '…' }
        Write-Host "ОТКАЗ: GET $relative -> $($r.StatusCode): $hint`n$body" -ForegroundColor Red
        exit 1
    }
    $r
}

$raw  = (Get-OData '$metadata').Content
# 200 с HTML (страница IIS, чужой сайт на порту) — это не OData, и сказать надо это, а не стек и не
# «пустой состав». Аккуратный HTML — корректный XML, поэтому судим по корню: у OData он `Edmx`.
$meta = $null
try { $meta = [xml]$raw } catch { }
if (-not $meta -or $meta.DocumentElement.LocalName -ne 'Edmx') {
    $head = ($raw | Out-String).Trim(); if ($head.Length -gt 300) { $head = $head.Substring(0, 300) + '…' }
    Write-Host "ОТКАЗ: `$metadata ответил 200, но это не описание OData (нет корня Edmx) — по адресу не OData 1С.`n$head" -ForegroundColor Red
    exit 1
}
$sets = @($meta.SelectNodes("//*[local-name()='EntitySet']") | ForEach-Object { $_.Name })
if ($sets.Count -eq 0) {
    # Пустой ответ бывает по трём причинам, и по виду они неотличимы — называем, что видно.
    $types = $meta.SelectNodes("//*[local-name()='EntityType']").Count
    Write-Host "ОТКАЗ: в `$metadata нет ни одного набора (ответ $($raw.Length) байт, типов $types)." -ForegroundColor Red
    Write-Host '  1. Состав стандартного интерфейса OData в базе не задан или не сохранён.'
    Write-Host '  2. Пул держит сеанс со старым составом — перезапуск: sudo pwsh -NoProfile -File scripts/1c-stand/publish-odata-iis.ps1'
    Write-Host "  3. У пользователя «$($cred.UserName)» нет права на объекты состава — 1С их не показывает."
    exit 1
}
Write-Host "Наборов в интерфейсе OData: $($sets.Count)" -ForegroundColor Green
$sets | Group-Object { ($_ -split '_')[0] } | Sort-Object Name |
    ForEach-Object { '  {0,-28} {1}' -f $_.Name, $_.Count }

# Чтение одной записи первого справочника — проверяет, что база открывается, а не только схема.
$probe = $sets | Where-Object { $_ -like 'Catalog_*' } | Select-Object -First 1
if (-not $probe) { $probe = $sets[0] }
$one = (Get-OData "${probe}?`$top=1&`$format=json").Content | ConvertFrom-Json
Write-Host "Чтение $probe : записей в ответе $(@($one.value).Count)" -ForegroundColor Green
exit 0   # явно: шаг `shell: pwsh` в Actions кончается `exit $LASTEXITCODE`
