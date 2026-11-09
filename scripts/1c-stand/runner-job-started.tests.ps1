<#
  Логика хука раннера 1С на поддельных событиях — без раннера и без сети.
  pwsh -NoProfile -File scripts/1c-stand/runner-job-started.tests.ps1   (код 1 — есть провал)

  Каждая дверь из DEV_NOTES («Раннер: только задания владельца») — отдельным случаем: убрать
  проверку в хуке — краснеет её случай.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runner-job-started.ps1')

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
if ($failed) { Write-Host "Провалов: $failed" -ForegroundColor Red; exit 1 }
Write-Host "Все $($cases.Count) случаев прошли." -ForegroundColor Green
