# Задачи по формированию параметра доступа HTTP и MCP

## 1. Контракт и адаптеры

- [x] 1.1 Добавить `IRequestDataAccessResolver` в API и подключить его в DI по D1
  ([AUTH-6], [CORE-24.1]). Проверить в штатном хосте, что оба интерфейса разрешаются в тот же
  экземпляр `DataAccessResolver`. Сравнить идентификатор, имя, ключи и включённые модули на
  том же принципале. Проверить извлечение принципала из HTTP-контекста и ошибку без запроса.
  Проверить фоновый вход для действующего, удалённого и отсутствующего автора, включая причину
  системного доступа. При замене запросной регистрации фоновый интерфейс по-прежнему разрешается
  в исходную реализацию.
- [x] 1.2 Подключить интерфейс в адаптерах таблиц, наборов и снимков по D2
  ([AUTH-8], [AUTH-9], [AUTH-12.1], [CORE-24.1], [WORK-40]). Сверить зависимости обработчиков
  по списку D2 и штатные ответы для `query`, ключа строки, XLSX, публикации таблицы,
  создания/просмотра источника, MCP `list_datasets`, `get_dataset`, `get_source`, `get_rows`
  и чтения двух ресурсов. Проверить сохранение ворот и поведения каталога метаданных.
- [x] 1.3 Поднять отдельный тестовый хост с `work` и подменой интерфейса по D3
  ([STG-6], [WORK-39], [WORK-40]). Доказать на синтетической БД, что при том же JWT подмена меняет
  состав строк и границу выдачи в реальных HTTP `query` и MCP `get_rows`, после снятия подмены
  возвращается исходная выдача. Проверить, что отказ ворот MCP без права
  чтения наборов сохраняется. Зафиксировать и очистить выделенные ресурсы локального прогона.
- [x] 1.4 Описать в разделе о параметре доступа системных наборов в `docs/DEV_NOTES.md`
  запросный контракт, отдельный фоновый вход и регистрацию подмены в тестовой фабрике.
  Сверить описание с рабочей конфигурацией интеграционного теста 1.3.

## 2. Верификация

- [x] 2.1 Из `src/server` запустить `dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter …`
  для новых проверок DI и хоста, `ContractPermissionTests`, `ModuleReaderTests`,
  `ModuleBoundaryTests`, `EndpointGateInventoryTests`, `McpGateInventoryTests`,
  `McpToolGateTests`, `IsolatedReadInventoryTests`, `IsolatedSourceStorageTests`,
  `ModuleTableIsolationPathsTests`, `WorkTableIsolationTests`, `ModuleTableQueryTests`,
  `TableExportTests`, `TableDataSetBridgeTests`, `DataSnapshotServiceTests`,
  `DocumentValidationMcpTests` и `WorkIsolationViolationTests`. Прогоны на отдельной тестовой БД.
- [x] 2.2 Из `src/server` выполнить `dotnet build BHS.CRG.slnx` и
  `dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet`.
  Из `src/client` выполнить `npx tsc -b` и `npm run ui:ratchet`.
- [x] 2.3 Из корня выполнить `openspec validate --all --strict --no-interactive` и
  `git diff --check`. Сверить PR реализации с границами change и критериями 1.1, 1.5 и 1.6 Work-25.
