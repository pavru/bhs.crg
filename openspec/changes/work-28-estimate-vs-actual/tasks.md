# Tasks: «по смете — фактически» и выработка

## 1. PR 1: сервер

- [ ] 1.1 `Labor/EstimateVsActual.cs`: `estimatedHours` по нормам контракта «Плановые работы», `actualHours`
  принятых отчётов, `offEstimate`; бригада — итогом группы [WORK-42]. Проверка — тесты: непринятый отчёт
  не входит; несметная работа в `offEstimate`; группа из трёх строк даёт один объём
- [ ] 1.2 Разнесение по разделу «объём × норма», `distributed: true`, строки без нормы — в `offEstimate`
  [WORK-42] [WORK-Q5]. Проверка — тест: сумма разнесённых часов по разделам равна часам отчётов
- [ ] 1.3 `Labor/Productivity.cs`: выработка по месяцам только по отчётам с одним видом и одной единицей,
  выборка, `minSample` из `IModuleSettings`, счётчики `discarded` [WORK-43] [WORK-Q6]. Проверка —
  тесты: ниже порога — `too-little-data` без числа; смешанный отчёт назван
- [ ] 1.4 Процент к норме по стройке, разделу и сотруднику — только внутри доступных строек [WORK-43]
  [WORK-39]. Проверка — тест: часы сотрудника на чужой стройке не входят в его процент
- [ ] 1.5 Состояния `ok`, `not-connected`, `hidden` с кодом права, `no-accepted`, `too-little-data`
  [WORK-42] [WORK-43] [CORE-34]. Проверка — каждое состояние получено настоящим путём (`plan`
  выключен, нет `plan.labor.read`, нет принятых, ниже порога)
- [ ] 1.6 `Endpoints/LaborEndpoints.cs`: ворота модуля, «любое из» `work.report.review` и
  `work.report.read.all`, изоляция [WORK-39] [AUTH-8]. Проверка — интеграционные тесты: чужая стройка —
  404, монтажник — 403, модуль выключен — 501; `EndpointGateInventoryTests` зелёный
- [ ] 1.7 Контракт `BHS.CRG.Modules/Exchange/IWorkProductivity.cs` под `work.facts.read`; реализация в
  `RegisterServices` [PLAN-16] [WORK-57]. Проверка — тест по ключам JSON: в ответе нет часов, отчётов и
  сотрудников; хост без `work` — «не подключено»; `ModuleBoundaryTests` зелёный
- [ ] 1.8 Тест состава `/api/account/access` по ролям, если `plan.labor.read` объявлено (D2) [WORK-57].
  Проверка — тест зелёный; без права у «Менеджера проекта» ответ `hidden`

## 2. PR 2: полоса в «Ходе работ» (макеты Р6)

- [ ] 2.1 Хук в `src/client/src/shared/api/` и `src/client/src/features/work/labor/EstimateVsActualStrip.tsx`
  по выбранному макету Р6: «по смете N чел.-ч — фактически M ч», «расчётная» при разделе, строка
  несметных [WORK-42] [PLAN-21]. Проверка — vitest: пометка из `distributed`, клиент не пересчитывает
- [ ] 2.2 Состояния — строкой с причиной, а не прочерком [WORK-42] [CORE-34]. Проверка — vitest на
  каждое `state`
- [ ] 2.3 Вклад полосы в «Ход работ» механизмом вклада модулей этапа 3; нет механизма — первым коммитом
  по образцу панелей карточки стройки (`work-03`) [PLAN-21]. Проверка — полоса пропадает при
  выключенном `work`, экран при этом цел
- [ ] 2.4 `docs/USER_GUIDE.md` — полоса, «расчётная», «данных мало»; содержание — `npm run toc` в
  `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkLabor|FullyQualifiedName~WorkProductivity|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Живой прогон под ролями модуля: `work-smoke.mjs` (`work-25`) дополнен полосой под менеджером
  (числа) и руководителем (чтение) — зелёный
