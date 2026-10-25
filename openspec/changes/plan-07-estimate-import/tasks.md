# Tasks: импорт сметы

## 1. PR 1: загрузка и отчёт разбора

- [x] 1.1 `Data/EstimateImport.cs` и миграция `EstimateImports` (`--context PlanDbContext`): состояние, вид,
  версия схемы, модель разбора, не разобранные, `xmin` [PLAN-7]. Проверка — миграция на пересозданной базе
- [x] 1.2 `POST /api/plan/imports` (файл, стройка, `estimateId` или `newEstimate` — решение В-12): файл
  в `IModuleBlobs`, запись импорта, операция `plan.import.parse` через `IModuleJobs` [PLAN-6] [PLAN-7].
  Проверка — тест: ответ — импорт в `parsing`, операция поставлена
- [x] 1.3 `Import/EstimateImportJob.cs`: разбор `plan-05`, состояние `parsed` или `rejected` с причиной;
  ВОР и КА — `parsed` с видом без сводки редакции [PLAN-7] [STG-12]. Проверка — тесты: образец ЛСР,
  неизвестная версия, образец ВОР
- [x] 1.4 `GET /api/plan/imports/{id}`: «N из M», не разобранные с причиной, сводка; итог — только с
  `plan.estimate.cost`; `DELETE` до коммита с `If-Match` [PLAN-7] [PLAN-11]. Проверка — тест сводки без
  права на себестоимость; 400/409 у `DELETE`
- [x] 1.5 Ворота: все адреса под `plan.estimate.edit` [PLAN-2] [AUTH-8]. Проверка — 403 с одним `.read`,
  отказ ворот у «Инженера ИД»; `EndpointGateInventoryTests` зелёный

## 2. PR 2: создание редакции

- [ ] 2.1 `POST /api/plan/imports/{id}/commit` с `If-Match`; при не разобранных без `acceptPartial` — 409 с
  числами [PLAN-7]. Проверка — `EstimateImportCommitTests`: 409 с «280 из 300», с согласием — редакция
- [ ] 2.2 Точка расширения `IEstimateRowMatcher` (по умолчанию ничего не подставляет) → позиции через
  `IModulePlanItems.FindOrCreateManyAsync` → редакция, строки, ресурсы одним `SaveChanges`; отказ ключа —
  строка несопоставленная с причиной [STG-6.1] [CORE-12] [CORE-4]. Проверка — внедрённый сбой между
  шагами и повтор: ни второй позиции, ни половинной редакции
- [ ] 2.3 Уникальный `import_key` у редакции; повтор коммита — та же редакция; номер редакции — следующий;
  первая — действующая (решение В-5) [PLAN-6] [PLAN-5.2]. Проверка — тест двойного коммита; тест
  импорта в смету с двумя редакциями
- [ ] 2.4 Журнал действий ядра: «импорт: смета, редакция N, строк M, не разобрано K» [CORE-28]. Проверка —
  `ActivityLogInventoryTests` видит действие
- [ ] 2.5 `docs/USER_GUIDE.md`: импорт сметы, частичный файл, повторный импорт; `cd docs/tools && npm run
  toc` [PLAN-6] [PLAN-7]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~EstimateImport|FullyQualifiedName~GrandSmeta|FullyQualifiedName~ModulePlanItemsPort|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ActivityLogInventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
