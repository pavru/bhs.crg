# Tasks: модель сметы

## 1. Записи и схема

- [ ] 1.1 `Data/Estimate.cs`, `EstimateRevision.cs`, `EstimateRow.cs`, `EstimateRowMaterial.cs`,
  `EstimateRowLabor.cs` в `PlanDbContext` (`xmin`, 409); миграция `Estimates`:
  `dotnet ef migrations add Estimates --project src/server/BHS.CRG.Modules.Plan --context PlanDbContext`
  [PLAN-1] [PLAN-3] [PLAN-9.1]. Проверка — на пересозданной базе миграция применяется; `ModuleSchema`-тесты
  зелёные
- [ ] 1.2 `PlanRecordTypes.cs`: открытый составной тип `ПозицияСметы` в `RecordTypes`; тэги `estimate.*` не
  объявлять [TYPE-16]. Проверка — тест: тип есть только при включённом модуле
- [ ] 1.3 `PlanReferences.cs`: объявления `CORE-34.1` на стройку, раздел, позицию перечня, вид работы,
  единицу, номенклатуру; новая ссылка на архивную запись — отказ [CORE-34.1] [CORE-34.4]. Проверка —
  `ModuleReferenceInventoryTests` зелёный
- [ ] 1.4 `Freeze(revision, reason)`; правка и удаление зафиксированной — 409 с производной [PLAN-5].
  Проверка — `EstimateRevisionTests` на тестовой фиксации

## 2. Адреса

- [ ] 2.1 Чтение под `plan.estimate.read`: `GET /api/plan/estimates?constructionId=`, `GET /estimates/{id}`,
  `GET /revisions/{id}/rows?cursor=` со строками и ресурсами, пометка `unmatched` [PLAN-1] [PLAN-4]
  [PLAN-9.1]. Проверка — тест чтения: смета с двумя редакциями, строка без вида работы помечена
- [ ] 2.2 Правка под `plan.estimate.edit`: `POST /estimates`, `PUT /estimates/{id}`, `DELETE /estimates/{id}`,
  `DELETE /revisions/{id}`; `If-Match` на всех, кроме заведения [PLAN-2] [AUTH-8] [AUTH-9]. Проверка — тесты:
  400 без `If-Match`, 409 устаревший, 403 без `.edit`, отказ ворот у «Инженера ИД»
- [ ] 2.3 `POST /estimates/{id}/current-revision`: первая редакция — сама, следующие — действием (ждёт
  решения В-5); журнал действий ядра; удаление действующей — 409 [PLAN-5.2] [CORE-28]. Проверка —
  `EstimateRevisionTests`; `ActivityLogInventoryTests` видит действие

## 3. Себестоимость

- [ ] 3.1 Объявить `plan.estimate.cost` с объяснением и `ReadAllMark.In`; без права ответ строк и смет не
  несёт стоимости, итогов и `data`, несёт `hidden` с кодом права [PLAN-11] [PLAN-23]. Проверка —
  `EstimateCostVisibilityTests`: обход JSON ответа целиком, ни одного денежного поля и ни одного поля `data`
- [ ] 3.2 `PlanRows.Current(constructionId)` и перепись `PlanRowsInventoryTests`: строки для сумм — только им
  [PLAN-5.2]. Проверка — образец прямого чтения в тесте переписи краснеет
- [ ] 3.3 Тест состава `/api/account/access` по системным ролям после объявления `.cost` [PLAN-23].
  Проверка — у «Сметчика» есть, у «Менеджера проекта» нет (ждёт решения В-1)
- [ ] 3.4 `docs/DEV_NOTES.md`: почему деньги и ключ строки — колонки, а сметное наполнение — блок под правом
  [CORE-15]. Проверка — ревью PR

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Plan.Estimate|FullyQualifiedName~PlanRowsInventory|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~RecordWriteGuardCoverage|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~AccountAccess"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
