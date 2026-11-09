<#
.SYNOPSIS
  Хук раннера 1С «задание началось»: пускает только задания владельца из своего репозитория.

.DESCRIPTION
  Раннер вызывает его (ACTIONS_RUNNER_HOOK_JOB_STARTED) ДО первого шага любого задания, и ненулевой
  код выхода роняет задание целиком — шаги не начинаются.

  Почему проверка здесь, а не в `if:` workflow: PR из форка исполняется с ТОЙ версией workflow, что
  лежит в форке, и `if:` там просто вычеркнут. Этот же файл лежит на машине, вне репозитория и вне
  каталога раннера, и учётная запись раннера его не пишет (register-runner.ps1) — задание не может
  его ни отключить, ни поправить для следующего задания.

  Пускает, только если ВСЁ верно:
    - репозиторий — тот, что в policy.json;
    - событие из списка разрешённых (pull_request_target и комментарии — нет никогда);
    - инициатор (GITHUB_ACTOR) и тот, кто запустил повторно (GITHUB_TRIGGERING_ACTOR), — в списке;
    - у PR: ветка в ЭТОМ репозитории (не форк) и автор PR — в списке.

  Политика — policy.json рядом: { "repository": "pavru/bhs.crg", "users": ["pavru"] }.
  Проверка логики — runner-job-started.tests.ps1 (поддельные события, без раннера).
#>
[CmdletBinding()]
param(
    [string]$PolicyPath = (Join-Path $PSScriptRoot 'policy.json'),
    [string]$EventPath  = $env:GITHUB_EVENT_PATH
)

$AllowedEvents = 'pull_request', 'push', 'workflow_dispatch', 'schedule'

function Get-JobRefusal {
    # Возвращает причину отказа или $null, если задание можно пускать. Чистая функция — для тестов.
    param($Policy, [hashtable]$Vars, $Payload)

    if ($Vars.GITHUB_REPOSITORY -ne $Policy.repository) {
        return "репозиторий «$($Vars.GITHUB_REPOSITORY)» — раннер обслуживает только $($Policy.repository)"
    }
    if ($Vars.GITHUB_EVENT_NAME -notin $AllowedEvents) {
        return "событие «$($Vars.GITHUB_EVENT_NAME)» не разрешено (можно: $($AllowedEvents -join ', '))"
    }
    foreach ($k in 'GITHUB_ACTOR', 'GITHUB_TRIGGERING_ACTOR') {
        $who = $Vars[$k]
        if ($k -eq 'GITHUB_ACTOR' -and -not $who) { return 'GITHUB_ACTOR пуст' }
        if ($who -and $who -notin $Policy.users) { return "$k «$who» не в списке владельцев раннера" }
    }
    if ($Vars.GITHUB_EVENT_NAME -eq 'pull_request') {
        $pr = $Payload.pull_request
        if (-not $pr) { return 'событие pull_request без pull_request в теле' }
        $head = $pr.head.repo.full_name
        if ($head -ne $Policy.repository) { return "PR из «$head», а не из $($Policy.repository) (форк)" }
        if ($pr.user.login -notin $Policy.users) { return "автор PR «$($pr.user.login)» не в списке владельцев раннера" }
    }
    $null
}

# Проверка идёт ВСЕГДА, кроме явной просьбы тестов. ⚠️ Отличать тесты по способу запуска нельзя:
# раннер сам подключает хук точкой (`pwsh -command ". '<путь>'"`), и прежняя проверка
# `$MyInvocation.InvocationName -ne '.'` в первом же прогоне (#1836) молча пропустила задание без
# проверки. Переменную ставят только тесты, перед своим подключением; у раннера её нет.
if (-not (Get-Variable -Name RunnerHookLibraryOnly -ValueOnly -ErrorAction SilentlyContinue)) {
    $ErrorActionPreference = 'Stop'
    try {
        $policy = Get-Content -Raw $PolicyPath | ConvertFrom-Json
        $payload = if ($EventPath -and (Test-Path $EventPath)) { Get-Content -Raw $EventPath | ConvertFrom-Json } else { $null }
        $envs = @{}
        foreach ($k in 'GITHUB_REPOSITORY', 'GITHUB_EVENT_NAME', 'GITHUB_ACTOR', 'GITHUB_TRIGGERING_ACTOR') {
            $envs[$k] = [Environment]::GetEnvironmentVariable($k)
        }
        $refusal = Get-JobRefusal -Policy $policy -Vars $envs -Payload $payload
    } catch {
        # Сломанная политика — отказ, а не пропуск: хук, который молча пускает при ошибке, не хук.
        $refusal = "хук не смог проверить задание: $($_.Exception.Message)"
    }
    if ($refusal) {
        Write-Host "::error title=Раннер 1С отказал::$refusal"
        exit 1
    }
    Write-Host "Раннер 1С: задание $($envs.GITHUB_EVENT_NAME) от $($envs.GITHUB_ACTOR) допущено."
}
