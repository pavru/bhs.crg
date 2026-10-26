# Tasks: носители экранов смет

Два PR, оба с меткой `version:minor`. Идёт перед PR 1 `plan-09-estimate-ui`. Решения владельца — в
`../plan-09-estimate-ui/design.md` («Макеты П1», 26.10.2026) и D3 `design.md`; вопросов без ответа нет.

## 1. PR 1: строка, редакция, импорт

- [x] 1.1 Правило вида позиции `work` / `material` (D2) по всем образцам ЛСР в
  `src/server/BHS.CRG.Tests/TestData/GrandSmeta/`: материал по КА, шифр сборника цен материалов; спорное —
  `work`; правило вписано в D2 [PLAN-4]. Проверка — тест на образцах: число строк-материалов каждой ЛСР
  совпадает с эталоном, ни одна расценка ГЭСН/ФЕР не стала материалом
- [x] 1.2 Колонки строки `number`, `subordinate`, `kind` и миграция `PlanDbContext`; `EstimateImportRows` берёт
  их из `LocalEstimatePosition`; `EstimateRowDto` отдаёт [PLAN-4] [PLAN-7]. Проверка — `Estimate*Tests`:
  номера «13» и «13.1», у «13.1» признак подчинённой; у прежних редакций `number` пуст
- [x] 1.3 Строка-материал (D2): без `unmatched`, вне счётчика и плана, с материалом в `materials[]` (из
  позиции, если ресурса нет); сопоставление с видом работы — 400 «это материал, а не работа» [PLAN-4]
  [PLAN-9.1]. Проверка — тесты: счётчик `unmatched` не считает материалы; 400 у строки и раздела не
  трогает материалов
- [x] 1.4 Неактивная позиция не импортируется (D3): `skipped[]` отчёта с `reason: inactive`, «N из M» и
  `acceptPartial` её не учитывают [PLAN-7]. Проверка — тест на образце с неактивной позицией (ЛСР ОС,СКУД):
  строк на одну меньше, отчёт называет пропущенную, редакция создаётся без `acceptPartial`
- [x] 1.5 Редакция: `materialsUnmatched`, `unitChanged`, `importId`, `partial` (D4) [PLAN-7] [PLAN-6.1].
  Проверка — тест: частичная редакция несёт 280 и 300 у читающего с одним `plan.estimate.read`

## 2. PR 2: адреса чтения и массовой правки

- [ ] 2.1 `GET /api/plan/revisions/{id}/sections` (D5) под `plan.estimate.read` [PLAN-6.1]. Проверка — тест:
  30 разделов одним ответом, счётчики сходятся со строками; `EndpointGateInventoryTests` зелёный
- [ ] 2.2 `GET /api/plan/estimates` без стройки — страницами (D6) [PLAN-20]. Проверка — тест: сметы двух
  строек, курсор; без `plan.estimate.cost` денег нет, `hidden` есть
- [ ] 2.3 `GET /api/plan/estimates/{id}/current-revision/preview` (D7) под `plan.estimate.edit` [PLAN-5.2].
  Проверка — тест: только изменившиеся виды работ, действующая не меняется; без `cost` денег нет; с одним
  `read` — 403
- [ ] 2.4 `GET /api/plan/unit-factor` (D8) правилом `RowMatching` [PLAN-6.1]. Проверка — тест: «100 шт» → «шт»
  = 100; «м» → «шт» — пусто с причиной
- [ ] 2.5 `POST /api/plan/revisions/{id}/lines/match` (D9): строки или разделы, одна фиксация, материалы —
  в `skipped` [PLAN-6.1] [AUTH-8] [AUTH-9]. Проверка — тесты: без `If-Match` 400, устаревший 409 без единой
  правки, с одним `read` 403, «Инженер ИД» — отказ ворот; сверх 1000 строк — 400 с числом
- [ ] 2.6 `GET /api/plan/revisions/{id}/materials` и `PUT …/materials/match` (D10) [PLAN-9.1] [AUTH-8]
  [AUTH-9]. Проверка — тесты: 14 ресурсов одной группы одним запросом; без `If-Match` 400, устаревший 409,
  с одним `read` 403; цены — только с `cost`

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Estimate|FullyQualifiedName~RateMatch|FullyQualifiedName~ResourceMatch|FullyQualifiedName~Inventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не тронут)
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
