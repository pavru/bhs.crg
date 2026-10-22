# Tasks: распределение сметы по разделам

## 1. PR 1: сервер

- [ ] 1.1 `Data/DistributionRule.cs`, `Data/DistributionPart.cs`, заголовок распределения редакции с `xmin`
  и `distributionChangedAt`; миграция `Distribution` (`--context PlanDbContext`) [PLAN-5.1]. Проверка — на
  пересозданной базе старт применяет миграцию
- [ ] 1.2 `Distribution/DistributionMath.cs`: действующее распределение строки (части → правило → «стройка
  в целом»), баланс, `distributed` / `undistributed` в обеих единицах [PLAN-5.1] [COST-13]. Проверка —
  `DistributionMathTests`: 1200 = 700 + 500; части сверх объёма — отказ; остаток — «стройка в целом»
- [ ] 1.3 Адреса `GET …/distribution`, `PUT …/rules`, `PUT/DELETE …/lines/{lineId}` под `plan.estimate.edit`
  с `If-Match`; правка у зафиксированной редакции разрешена [PLAN-5.1] [PLAN-5] [AUTH-8] [AUTH-9].
  Проверка — тесты отказов: без `If-Match` — 400, устаревший — 409, только `plan.estimate.read` — 403,
  модуль выключен — 501; `EndpointGateInventoryTests` зелёный
- [ ] 1.4 Журнал: действия `plan.distribution.*` через `IModuleActivityLog` с прежним и новым значением
  [PLAN-5.1] [CORE-28]. Проверка — тест: правка зафиксированной редакции дала запись журнала;
  `ActivityLogInventoryTests` зелёный
- [ ] 1.5 Перенос при повторном импорте — шаг `plan-07`: `carried`, `ambiguous`, `lost` [PLAN-5.1].
  Проверка — `DistributionCarryOverTests`: шифр в двух разделах ЛСР переносится по разделам; пропавшая
  строка — в `lost`
- [ ] 1.6 Объявления ссылок `CORE-34.1` на раздел стройки [CORE-34.1]. Проверка —
  `ModuleReferenceInventoryTests` зелёный; удаление занятого раздела — отказ с числом строк

## 2. PR 2: экран

- [ ] 2.1 Раунд П2: разбор с Дизайнером, три варианта в Claude Design (разделы ЛСР → разделы стройки,
  исключения, «Распределено», отчёт переноса), выбор владельца; ссылку вписать в `design.md` [PLAN-5.1].
  Проверка — ссылка на выбранный вариант в `design.md`
- [ ] 2.2 `shared/api/planningDistribution.ts` с `If-Match` и разбором 400/403/409 [PLAN-5.1]. Проверка —
  vitest на разбор отказов
- [ ] 2.3 `features/planning/distribution/*` на `DataGrid`: правило по разделу ЛСР, исключения, колонка
  «Распределено», остаток «стройка в целом», отчёт переноса [PLAN-5.1]. Проверка — vitest: 409 —
  «перечитайте» с сохранением набранного; `lost` и `ambiguous` показаны списком
- [ ] 2.4 Живой прогон: распределение в `e2e/planning-estimates-smoke.mjs` [PLAN-5.1]. Проверка — прогон
  зелёный на стенде
- [ ] 2.5 `docs/USER_GUIDE.md`: распределение по разделам; `npm run toc` в `docs/tools`. Проверка —
  проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Distribution|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~ActivityLogInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
