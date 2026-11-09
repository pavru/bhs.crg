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
      учётной записи раннера: задание не может отключить проверку для следующего задания;
    - служба работает под отдельной локальной учётной записью без прав администратора;
    - этой записи ЗАПРЕЩЁН доступ к каталогам с данными и исходниками (-DenyPaths): к базе 1С раннер
      ходит только по HTTP через публикацию OData, а не файлами;
    - пароль записи случайный, нигде не показывается и не сохраняется: он нужен один раз — службе.

  Повторный запуск на уже зарегистрированном раннере не регистрирует его заново, а обновляет хук,
  политику и права — так меняют список -AllowedUsers.

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
    [string[]]$DenyPaths  = @('E:\Accounting', 'E:\Development\Office')
)

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $RunnerDir 'config.cmd'))) { throw "В $RunnerDir нет config.cmd — раннер не распакован." }
$registered = Test-Path (Join-Path $RunnerDir '.runner')
if (-not $registered -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Нет gh в PATH.' }

$qualified = "$env:COMPUTERNAME\$Account"
# Администраторы и SYSTEM — по SID: имена групп локализованы («Администраторы»).
$admins = '*S-1-5-32-544'; $system = '*S-1-5-18'

# Закрыть путь от записи учётной записи раннера: наследование снято, ей — только чтение.
function Protect-Path($path, $rights) {
    & icacls $path /inheritance:r /grant:r "${admins}:$rights(F)" "${system}:$rights(F)" "${qualified}:$rights(RX)" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $path -> $LASTEXITCODE" }
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
    if (Test-Path $p) { & icacls $p /deny "${qualified}:(OI)(CI)F" /Q | Out-Null; Write-Host "    запрещено: $p" }
    else { Write-Warning "Нет $p — пропускаю." }
}

Step "Права на каталог раннера $RunnerDir"
& icacls $RunnerDir /grant "${qualified}:(OI)(CI)M" /Q | Out-Null

# --- Хук ---------------------------------------------------------------------------------------
Step "Хук «задание началось» в $HookDir (пускает: $($AllowedUsers -join ', ') из $Repo)"
New-Item -ItemType Directory -Force $HookDir | Out-Null
Protect-Path $HookDir '(OI)(CI)'
$hook = Join-Path $HookDir 'job-started.ps1'
# С BOM: раннер может звать хук Windows PowerShell 5.1, а тот читает UTF-8 без BOM как cp1251 —
# и кириллица в строках превращается в «умные кавычки», ломающие разбор.
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'runner-job-started.ps1'))
[IO.File]::WriteAllText($hook, $src, [Text.UTF8Encoding]::new($true))
@{ repository = $Repo; users = @($AllowedUsers) } | ConvertTo-Json |
    Set-Content -Path (Join-Path $HookDir 'policy.json') -Encoding utf8NoBOM

# --- Регистрация -------------------------------------------------------------------------------
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
}

# --- .env раннера: называет хук, закрыт от записи ----------------------------------------------
Step '.env раннера: ACTIONS_RUNNER_HOOK_JOB_STARTED'
$envFile = Join-Path $RunnerDir '.env'
$lines = if (Test-Path $envFile) { @(Get-Content $envFile | Where-Object { $_ -notmatch '^ACTIONS_RUNNER_HOOK_JOB_STARTED=' }) } else { @() }
$lines += "ACTIONS_RUNNER_HOOK_JOB_STARTED=$hook"
Set-Content -Path $envFile -Value $lines -Encoding utf8NoBOM
Protect-Path $envFile ''

# .env читается при старте службы — без перезапуска хук не действует.
$svc = Get-Service -Name 'actions.runner.*' | Where-Object { $_.Name -like "*$RunnerName*" }
if (-not $svc) { throw "Служба раннера $RunnerName не найдена." }
Restart-Service -InputObject $svc
$svc.Refresh()

Write-Host ''
Write-Host "Готово. Служба: $($svc.Name) — $($svc.Status); хук: $hook" -ForegroundColor Green
Write-Host "Проверка: gh api repos/$Repo/actions/runners --jq '.runners[] | [.name,.status] | @tsv'"
