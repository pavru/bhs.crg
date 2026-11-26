# Tasks: «по смете — фактически» и выработка

## 1. PR 1: сервер

- [x] 1.1 `Labor/EstimateVsActual.cs`: `estimatedHours` по нормам контракта «Плановые работы», `actualHours`
  принятых отчётов, `offEstimate`; бригада — итогом группы [WORK-42]. Проверка — тесты: непринятый отчёт
  не входит; несметная работа в `offEstimate`; группа из трёх строк даёт один объём
- [x] 1.2 Разнесение по разделу «объём × норма», `distributed: true`, строки без нормы — в `offEstimate`
  [WORK-42] [WORK-Q5]. Проверка — часы сохраняются в сумме разделов, работ без раздела, несметных и остатка
- [x] 1.3 `Labor/Productivity.cs`: выработка по месяцам только по отчётам с одним видом и одной единицей,
  выборка, `minSample` из `IModuleSettings`, счётчики `discarded` [WORK-43] [WORK-Q6]. Проверка —
  тесты: ниже порога — `too-little-data` без числа для итога и отдельных месяцев; смешанный отчёт назван
- [x] 1.4 Процент к норме по стройке, разделу и сотруднику — только внутри доступных строек [WORK-43]
  [WORK-39]. Проверка — часы сотрудника на чужой стройке не входят в его процент, сметный вес бригады делится по долям часов
- [x] 1.5 Состояния `ok`, `not-connected`, `hidden` с кодом права, `no-accepted`, `no-norms`, `too-little-data`
  [WORK-42] [WORK-43] [CORE-34]. Проверка — каждое состояние получено настоящим путём (`plan`
  выключен, нет доступа к нормам, нет принятых, нет норм, ниже порога)
- [x] 1.6 `Endpoints/LaborEndpoints.cs`: ворота модуля, «любое из» `work.report.review` и
  `work.report.read`, изоляция [WORK-39] [AUTH-8]. Проверка — интеграционные тесты: чужая стройка —
  404, монтажник — 403, модуль выключен — 501; переписи ворот и изоляции Work зелёные
- [x] 1.7 Контракт `BHS.CRG.Modules/Exchange/IWorkProductivity.cs` под `work.facts.read`; реализация в
  `RegisterServices` [PLAN-16] [WORK-57]. Проверка — тест по ключам JSON: в ответе нет часов, отчётов и
  сотрудников; один сотрудник не проходит порог; хост без `work` — «не подключено»; каталог и тестовая проба Plan согласованы
- [x] 1.8 Тест состава `/api/account/access` по ролям (D2) [WORK-57]. Проверка — менеджер получает
  `plan.labor.read`, руководитель читает нормы по `plan.estimate.read`, менеджер без доступа к нормам получает `hidden`

## 2. PR 2: вклады в «Ход работ» и в график (макеты Р6)

- [ ] 2.1 Макеты Р6 — полоса «по смете — фактически» и выработка у работы графика: выбор владельца
  ссылкой в `design.md` [WORK-42] [PLAN-16]. Проверка — ссылка вписана до 2.2
- [ ] 2.2 `IProgressContribution` (`plan-25`) в `RegisterServices` модуля `work`: полоса «по смете N
  чел.-ч — фактически M ч», «расчётная» при разделе, строка несметных, состояние строкой с причиной
  [WORK-42] [PLAN-21] [CORE-34]. Проверка — `ConstructionProgressTests` на хосте `plan` + `work`: полоса
  есть, при отборе по разделу — «расчётная»; без `plan.labor.read` — состояние с кодом права; при
  выключенном `work` полосы нет, экран цел
- [ ] 2.3 Ответ графика `plan` (`plan-19`): у работы `productivity` из контракта «Выработка» с состоянием;
  экран графика (`plan-20`) — рядом с объёмом работы по макету Р6 [PLAN-16] [WORK-43]. Проверка — тест
  ответа графика: выключенный `work` — «не подключено», без `work.facts.read` — «скрыто правами», ниже
  порога — «данных мало»; vitest экрана графика на каждое состояние
- [ ] 2.4 `docs/USER_GUIDE.md` — полоса, «расчётная», «данных мало», выработка у работы графика; содержание — `npm run toc` в
  `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkLabor|FullyQualifiedName~WorkProductivity|FullyQualifiedName~ConstructionProgress|FullyQualifiedName~Schedule|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Живой прогон под ролями модуля: `work-smoke.mjs` (`work-25`) дополнен полосой под менеджером
  (числа) и руководителем (чтение) — зелёный
