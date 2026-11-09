#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Регистрирует self-hosted раннер GitHub для работ с 1С — узкий: своя метка, своя учётная запись,
  хук, пускающий только задания владельца.

.DESCRIPTION
  Репозиторий ПУБЛИЧНЫЙ, а раннер исполняет код workflow на этой машине (DEV_NOTES, «Раннер: только
  задания владельца»). Поэтому:
    - метка `1c`: на раннер попадают только работы с `runs-on: [self-hosted, 1c]`;
    - хук «задание началось» (runner-job-started.ps1) роняет до первого шага всё, что не от
      -AllowedUsers из своего репозитория, — в том числе PR из форка, который переписал workflow.
      Хук и его политика лежат в -HookDir, а `.env` раннера, который его называет, закрыт от записи
      учётной записи раннера: задание не может отключить проверку для следующего задания.
      Хук и `.env` пишутся ДО регистрации: служба стартует из config.cmd сразу, и задание из очереди
      иначе успело бы пройти без хука;
    - служба работает под отдельной локальной учётной записью без прав администратора;
    - этой записи ЗАПРЕЩЁН доступ к каталогам с данными и исходниками (-DenyPaths): к базе 1С раннер
      ходит только по HTTP через публикацию OData, а не файлами;
    - пароль записи случайный и нигде не сохраняется: он нужен один раз — службе. ⚠️ На время
      регистрации он виден в командной строке config.cmd (другого способа передать его у config.cmd
      нет), то есть в списке процессов — для того, у кого есть право его читать.

  `.env` раннера несёт и правила машины для всех заданий: системный gitconfig здесь включает
  встроенный fsmonitor, и checkout падал на EBUSY (#1836) — отключён переменными GIT_CONFIG_*.

  Повторный запуск на уже зарегистрированном раннере не регистрирует его заново, а обновляет хук,
  политику, `.env` и права — так меняют список -AllowedUsers и обновляют хук после правки. Службу он
  перезапускает, только дождавшись, пока раннер освободится: перезапуск обрывает идущее задание.

  Токен регистрации берёт сам через `gh` (нужен вход `gh auth login` с правом admin на репозиторий).

  Запускать с -NoProfile: модуль WinGet CommandNotFound из профиля в окне `sudo` падает при прогреве
  в фоновом потоке и роняет весь процесс посреди скрипта (наступили 09.11.2026 — на icacls).

.EXAMPLE
  sudo pwsh -NoProfile -File scripts/1c-stand/register-runner.ps1
#>
[CmdletBinding()]
param(
    [string]$RunnerDir    = 'E:\Development\actions-runner',
    [string]$Repo         = 'pavru/bhs.crg',
    [string[]]$AllowedUsers = @('pavru'),
    [string]$Account      = 'gh-runner-1c',
    [string]$RunnerName   = "bhs-1c-$($env:COMPUTERNAME.ToLower())",
    [string]$Labels       = '1c',
    [string]$HookDir      = (Join-Path $env:ProgramData 'gh-runner-1c'),
    [string[]]$DenyPaths  = @('E:\Accounting', 'E:\Development\Office'),
    [int]$BusyWaitMinutes = 15
)

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $RunnerDir 'config.cmd'))) { throw "В $RunnerDir нет config.cmd — раннер не распакован." }
$registered = Test-Path (Join-Path $RunnerDir '.runner')
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Нет gh в PATH (нужен и для регистрации, и чтобы узнать, занят ли раннер).' }

$qualified = "$env:COMPUTERNAME\$Account"
# Администраторы и SYSTEM — по SID: имена групп локализованы («Администраторы»).
$admins = '*S-1-5-32-544'; $system = '*S-1-5-18'
$hook    = Join-Path $HookDir 'job-started.ps1'
$envFile = Join-Path $RunnerDir '.env'

# Закрыть путь от записи учётной записи раннера: наследование снято, ей — только чтение.
function Protect-Path($path, $rights) {
    & icacls $path /inheritance:r /grant:r "${admins}:$rights(F)" "${system}:$rights(F)" "${qualified}:$rights(RX)" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $path -> $LASTEXITCODE" }
}

# Явный запрет уже стоит? Повторная установка не нужна: icacls распространяет его по всему дереву, а
# у E:\Development\Office это минуты (исходники, node_modules) — на каждом обновлении хука.
function Test-DenyAce($path) {
    @((Get-Acl $path).Access | Where-Object {
        $_.IdentityReference.Value -eq $qualified -and $_.AccessControlType -eq 'Deny' -and -not $_.IsInherited -and
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl
    }).Count -gt 0
}

function Write-RunnerEnv {
    # Наши ключи переписываются, чужие строки .env остаются как были.
    $ours = [ordered]@{
        ACTIONS_RUNNER_HOOK_JOB_STARTED = $hook
        GIT_CONFIG_COUNT   = '2'
        GIT_CONFIG_KEY_0   = 'core.fsmonitor'
        GIT_CONFIG_VALUE_0 = 'false'
        GIT_CONFIG_KEY_1   = 'core.useBuiltinFSMonitor'
        GIT_CONFIG_VALUE_1 = 'false'
    }
    $keep = if (Test-Path $envFile) {
        @(Get-Content $envFile | Where-Object { $_ -and ($_ -split '=', 2)[0] -notin $ours.Keys })
    } else { @() }
    $lines = $keep + @($ours.Keys | ForEach-Object { "$_=$($ours[$_])" })
    Set-Content -Path $envFile -Value $lines -Encoding utf8NoBOM
    Protect-Path $envFile ''
    $lines -join "`n"
}

function Get-RunnerBusy {
    $busy = gh api "repos/$Repo/actions/runners" --jq ".runners[] | select(.name == `"$RunnerName`") | .busy" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $busy) { return $null }   # не знаем — решает вызывающий
    $busy -eq 'true'
}

# --- Учётная запись ----------------------------------------------------------------------------
if (-not $registered) {
    Step "Локальная учётная запись $Account (без прав администратора)"
    # 24 случайных байта в hex + знаки других классов — проходит политику сложности. Только
    # [0-9a-fZ_]: пароль едет через config.cmd, а cmd портит в аргументах `!`, `%`, `^`, `=`.
    $bytes = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $plain = [Convert]::ToHexString($bytes).ToLower() + 'Z_9'
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force

    if (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue) {
        Set-LocalUser -Name $Account -Password $secure -PasswordNeverExpires $true
    } else {
        New-LocalUser -Name $Account -Password $secure -PasswordNeverExpires -UserMayNotChangePassword `
            -AccountNeverExpires -Description 'GitHub Actions runner (1C, bhs.crg)' | Out-Null
    }
}
if (Get-LocalGroupMember -SID 'S-1-5-32-544' | Where-Object { $_.Name -like "*\$Account" }) {
    throw "$Account состоит в администраторах — так раннер не регистрируем."
}

Step 'Запрет доступа к данным и исходникам'
foreach ($p in $DenyPaths) {
    if (-not (Test-Path $p)) { Write-Warning "Нет $p — пропускаю."; continue }
    if (Test-DenyAce $p) { Write-Host "    уже запрещено: $p"; continue }
    & icacls $p /deny "${qualified}:(OI)(CI)F" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /deny $p -> $LASTEXITCODE" }
    Write-Host "    запрещено: $p"
}

Step "Права на каталог раннера $RunnerDir"
& icacls $RunnerDir /grant "${qualified}:(OI)(CI)M" /Q | Out-Null

# --- Хук и .env — до регистрации ---------------------------------------------------------------
Step "Хук «задание началось» в $HookDir (пускает: $($AllowedUsers -join ', ') из $Repo)"
New-Item -ItemType Directory -Force $HookDir | Out-Null
Protect-Path $HookDir '(OI)(CI)'
# С BOM: раннер может звать хук Windows PowerShell 5.1, а тот читает UTF-8 без BOM как cp1251 —
# и кириллица в строках превращается в «умные кавычки», ломающие разбор.
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'runner-job-started.ps1'))
[IO.File]::WriteAllText($hook, $src, [Text.UTF8Encoding]::new($true))
@{ repository = $Repo; users = @($AllowedUsers) } | ConvertTo-Json |
    Set-Content -Path (Join-Path $HookDir 'policy.json') -Encoding utf8NoBOM

Step '.env раннера: хук и правила git для всех заданий'
$written = Write-RunnerEnv

# --- Регистрация -------------------------------------------------------------------------------
$needRestart = $registered
if (-not $registered) {
    Step "Регистрация в $Repo как $RunnerName (метки: self-hosted, Windows, X64, $Labels)"
    $token = gh api -X POST "repos/$Repo/actions/runners/registration-token" --jq .token
    if (-not $token) { throw 'gh не выдал токен регистрации (нужно право admin на репозиторий).' }
    Push-Location $RunnerDir
    try {
        & .\config.cmd --unattended --url "https://github.com/$Repo" --token $token `
            --name $RunnerName --labels $Labels --work _work --replace `
            --runasservice --windowslogonaccount $qualified --windowslogonpassword $plain
        if ($LASTEXITCODE -ne 0) { throw "config.cmd -> $LASTEXITCODE" }
    } finally {
        Pop-Location
        $plain = $null; $token = $null; $secure = $null
    }
    # Служба уже стартовала с .env, записанным выше. Если config.cmd его тронул — переписать и
    # перезапустить, иначе первое задание пошло бы без хука.
    $now = if (Test-Path $envFile) { (Get-Content $envFile) -join "`n" } else { '' }
    if ($now -ne $written) {
        Write-Warning 'config.cmd изменил .env — переписываю и перезапускаю службу.'
        Write-RunnerEnv | Out-Null
        $needRestart = $true
    }
}

$svc = Get-Service -Name 'actions.runner.*' | Where-Object { $_.Name -like "*$RunnerName*" }
if (-not $svc) { throw "Служба раннера $RunnerName не найдена." }

if ($needRestart) {
    # .env читается при старте службы — без перезапуска новый хук не действует. Но перезапуск обрывает
    # идущее задание, поэтому сначала ждём, пока раннер освободится.
    Step 'Перезапуск службы (новый .env и хук)'
    $deadline = (Get-Date).AddMinutes($BusyWaitMinutes)
    while ($true) {
        $busy = Get-RunnerBusy
        if ($null -eq $busy) { Write-Warning 'Не удалось узнать у GitHub, занят ли раннер, — перезапускаю как есть.'; break }
        if (-not $busy) { break }
        if ((Get-Date) -gt $deadline) {
            throw "Раннер занят заданием дольше $BusyWaitMinutes мин. Хук и .env записаны, но служба НЕ перезапущена — новый хук не действует. Повторите, когда раннер освободится."
        }
        Write-Host '    раннер выполняет задание — жду…'
        Start-Sleep -Seconds 15
    }
    Restart-Service -InputObject $svc
    $svc.Refresh()
}

Write-Host ''
Write-Host "Готово. Служба: $($svc.Name) — $($svc.Status); хук: $hook" -ForegroundColor Green
Write-Host "Проверка: gh api repos/$Repo/actions/runners --jq '.runners[] | [.name,.status] | @tsv'"
