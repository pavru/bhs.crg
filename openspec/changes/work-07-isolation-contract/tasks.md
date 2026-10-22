# Tasks: контракт изоляции

## 1. PR 1: параметр доступа и граница выдачи

- [ ] 1.1 `BHS.CRG.Modules/Access/ModuleReach.cs`; построение `ModuleReader` (готовый, из `plan-22`) из
  `DataAccess` в `Api` [WORK-40] [CORE-24.1]. Проверка — `ModulePortMirrorTests` сверяет зеркало,
  `ModuleBoundaryTests` зелёный
- [ ] 1.2 `ModuleTableQuery.UserId` → `Reader`; `ModuleTableService.cs:74` и `ModuleTableService.Shortcuts.cs:44`
  без `UserId!.Value`; система у `PerUser`-таблицы — отказ с причиной [WORK-40] [CORE-24.1] [WORK-41].
  Проверка — `ModuleTableIsolationTests` на пробном модуле: задание без человека получает отказ с текстом
  причины, а не исключение и не пустую страницу
- [ ] 1.3 `ModuleTable.Boundaries` (код, текст с `{sites}`), `ModuleTablePage.Outcome`; `Problems()`
  отвергает пустой список и повтор кода [CORE-24.3]. Проверка — `ModuleTableCatalogTests`: такое
  объявление не стартует с названием таблицы
- [ ] 1.4 Ответ таблицы: `boundary` применившегося исхода и `boundaryOutcome`; не названный исход у
  таблицы с несколькими — отказ; первая строка XLSX (`ModuleTableExport.cs`); набор
  (`ModuleTableDataProvider.cs`) и MCP [CORE-24.3] [AUTH-12.1]. Проверка — `ModuleTableIsolationTests`:
  экран, XLSX, набор и MCP дают одни строки и одну строку «… ваших строек (4)»; `McpGateInventoryTests` зелёный
- [ ] 1.5 Чужой ключ строки (`row`) под отбором по правам — 404, как несуществующий [WORK-39].
  Проверка — тест на пробном модуле
- [ ] 1.6 `InvoiceTable.cs` — одна граница; клиент `shared/api/tables.ts` — `boundaryOutcome` [CORE-24.3].
  Проверка — `SystemDataSetDeclarationTests` и тесты таблицы счетов зелёные без правки ожиданий

## 2. PR 2: перепись чтения изолированных записей

- [ ] 2.1 `BHS.CRG.Modules/Data/IIsolatedRecord.cs` [WORK-39] [CORE-3]. Проверка — сборка решения
- [ ] 2.2 `Tests/Configuration/IsolatedReadInventoryTests.cs`: нет `DbSet<T>` у изолированной записи;
  `Set<T>()` и сырой SQL с её таблицей — только в файле из списка с причиной; `PerUser`-таблица без
  изолированной записи — красный [WORK-39] [WORK-40] [CORE-3]. Проверка — тест сначала красный на
  образцах исходника с чтением в обход (каждое из трёх нарушений), затем зелёный на решении
- [ ] 2.3 Вписать перепись в `InventoryScopeTests` (проекты через `SolutionModules.WithCore`) [CORE-3].
  Проверка — `InventoryScopeTests` зелёный
- [ ] 2.4 `docs/DEV_NOTES.md`: изолированная запись — пометка, файл чтения, параметр доступа и охват;
  почему не `HasQueryFilter`. Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModuleTable|FullyQualifiedName~IsolatedRead|FullyQualifiedName~SystemDataSet|FullyQualifiedName~McpGateInventory|FullyQualifiedName~ModulePortMirror|FullyQualifiedName~InventoryScope|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
