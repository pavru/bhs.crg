<#
  Логика хука раннера 1С на поддельных событиях — без раннера и без сети.
  pwsh -NoProfile -File scripts/1c-stand/runner-job-started.tests.ps1   (код 1 — есть провал)

  Каждая дверь из DEV_NOTES («Раннер: только задания владельца») — отдельным случаем: убрать
  проверку в хуке — краснеет её случай.
#>
$ErrorActionPreference = 'Stop'
$hookPath = Join-Path $PSScriptRoot 'runner-job-started.ps1'
$RunnerHookLibraryOnly = $true   # только функция, без проверки окружения этого процесса
. $hookPath
Remove-Variable RunnerHookLibraryOnly

$policy = [pscustomobject]@{ repository = 'pavru/bhs.crg'; users = @('pavru') }

function Vars($event, $actor = 'pavru', $triggering = 'pavru', $repo = 'pavru/bhs.crg') {
    @{ GITHUB_REPOSITORY = $repo; GITHUB_EVENT_NAME = $event; GITHUB_ACTOR = $actor; GITHUB_TRIGGERING_ACTOR = $triggering }
}
function Pr($headRepo = 'pavru/bhs.crg', $author = 'pavru') {
    [pscustomobject]@{ pull_request = [pscustomobject]@{
        head = [pscustomobject]@{ repo = $(if ($headRepo) { [pscustomobject]@{ full_name = $headRepo } } else { $null }) }
        user = [pscustomobject]@{ login = $author } } }
}

$cases = @(
    @{ n = 'свой PR владельца — пускает';               v = (Vars pull_request);                 p = (Pr);                     ok = $true }
    @{ n = 'ручной запуск владельца — пускает';         v = (Vars workflow_dispatch);            p = $null;                    ok = $true }
    @{ n = 'push владельца — пускает';                  v = (Vars push);                         p = $null;                    ok = $true }
    @{ n = 'schedule владельца — пускает';              v = (Vars schedule);                     p = $null;                    ok = $true }
    @{ n = 'пустой инициатор — отказ';                  v = (Vars schedule '' '');               p = $null;                    ok = $false; m = 'пуст' }
    @{ n = 'PR из форка — отказ';                       v = (Vars pull_request);                 p = (Pr 'evil/bhs.crg');      ok = $false; m = 'форк' }
    @{ n = 'PR из удалённого форка — отказ';            v = (Vars pull_request);                 p = (Pr $null);               ok = $false }
    @{ n = 'PR чужого автора из своей ветки — отказ';   v = (Vars pull_request);                 p = (Pr 'pavru/bhs.crg' 'x'); ok = $false; m = 'автор PR' }
    @{ n = 'чужой инициатор — отказ';                   v = (Vars push 'mallory');               p = $null;                    ok = $false; m = 'GITHUB_ACTOR' }
    @{ n = 'перезапуск чужим — отказ';                  v = (Vars pull_request 'pavru' 'mallory'); p = (Pr);                  ok = $false; m = 'GITHUB_TRIGGERING_ACTOR' }
    @{ n = 'pull_request_target — отказ всегда';        v = (Vars pull_request_target);          p = (Pr);                     ok = $false; m = 'не разрешено' }
    @{ n = 'issue_comment — отказ';                     v = (Vars issue_comment);                p = $null;                    ok = $false }
    @{ n = 'чужой репозиторий — отказ';                 v = (Vars push 'pavru' 'pavru' 'evil/x'); p = $null;                   ok = $false; m = 'репозиторий' }
    @{ n = 'pull_request без тела — отказ';             v = (Vars pull_request);                 p = $null;                    ok = $false }
)

$failed = 0
foreach ($c in $cases) {
    $r = Get-JobRefusal -Policy $policy -Vars $c.v -Payload $c.p
    $pass = if ($c.ok) { $null -eq $r } else { $null -ne $r -and (-not $c.m -or $r -match [regex]::Escape($c.m)) }
    if ($pass) { Write-Host "  ok   $($c.n)" }
    else { $failed++; Write-Host "  FAIL $($c.n) -> $(if ($r) { $r } else { 'допущено' })" -ForegroundColor Red }
}
# --- Хук целиком, ТЕМ ЖЕ вызовом, что у раннера --------------------------------------------------
# Функция может быть верной, а хук — не проверять ничего: так и было в первом прогоне (#1836) —
# раннер подключает хук точкой, а хук принимал точку за тесты и выходил молча. Поэтому отдельный
# процесс, команда из журнала раннера («Run 'C:\ProgramData\gh-runner-1c\job-started.ps1'»:
# shell: pwsh -command ". '{0}'»), и смотрим на код выхода.
$tmp = Join-Path ([IO.Path]::GetTempPath()) "hooktest-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Copy-Item $hookPath (Join-Path $tmp 'job-started.ps1')
    '{"repository":"pavru/bhs.crg","users":["pavru"]}' | Set-Content (Join-Path $tmp 'policy.json')
    $runs = @(
        @{ n = 'как раннер: PR из форка — код 1'; actor = 'pavru'; head = 'evil/bhs.crg'; code = 1 }
        @{ n = 'как раннер: свой PR — код 0';     actor = 'pavru'; head = 'pavru/bhs.crg'; code = 0 }
        @{ n = 'как раннер: нет policy.json — код 1'; actor = 'pavru'; head = 'pavru/bhs.crg'; code = 1; nopolicy = $true }
    )
    foreach ($r in $runs) {
        $ev = Join-Path $tmp 'event.json'
        @{ pull_request = @{ head = @{ repo = @{ full_name = $r.head } }; user = @{ login = 'pavru' } } } |
            ConvertTo-Json -Depth 5 | Set-Content $ev
        $hook = Join-Path $tmp 'job-started.ps1'
        if ($r.nopolicy) { $hook = Join-Path $tmp 'alone\job-started.ps1'; New-Item -ItemType Directory -Force (Split-Path $hook) | Out-Null; Copy-Item $hookPath $hook }
        $saved = @{}
        $vars = @{ GITHUB_REPOSITORY = 'pavru/bhs.crg'; GITHUB_EVENT_NAME = 'pull_request'; GITHUB_ACTOR = $r.actor
                   GITHUB_TRIGGERING_ACTOR = $r.actor; GITHUB_EVENT_PATH = $ev }
        foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
        try { $out = & pwsh -NoProfile -command ". '$hook'" 2>&1; $code = $LASTEXITCODE }
        finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
        if ($code -eq $r.code) { Write-Host "  ok   $($r.n)" }
        else { $failed++; Write-Host "  FAIL $($r.n) -> код $code`n$out" -ForegroundColor Red }
    }
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

if ($failed) { Write-Host "Провалов: $failed" -ForegroundColor Red; exit 1 }
Write-Host "Все $($cases.Count + $runs.Count) случаев прошли." -ForegroundColor Green
# Явный код: шаг `shell: pwsh` в Actions кончается `exit $LASTEXITCODE`, а последний нативный вызов
# выше — нарочно падающий хук (код 1). Без этой строки зелёные тесты дают красный шаг (#1836).
exit 0
