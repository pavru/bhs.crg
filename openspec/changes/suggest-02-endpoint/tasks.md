# Tasks: адрес `GET /api/field-suggestions`, источник верхнего уровня

PR закрывает #1530 («Closes #1530»), метка `version:minor`. `<Version>` не трогать. Условие — слит
`suggest-01`.

## 0. До кода

- [x] 0.1 (22.10.2026: приняты рекомендации (а), спека под них) Решения В-1 (право в шаге «Новая позиция»), В-2 (что клиент называет в `type`), В-3 (записи
  уровня «вся система») — согласованы с Архитектором и владельцем и записаны в
  `../suggest-00-epic/design.md` с пометкой «Решено»; `design.md` и спека этого change поправлены, если
  решение не совпало с рекомендацией. Проверка — пометки есть

## 1. Индекс и чтение

- [ ] 1.1 Индекс `IX_domain_objects_CompositeTypeId_UpdatedAt_Live` в `DomainObjectConfiguration.cs` и
  миграция: `dotnet ef migrations add FieldSuggestionsIndex --project src/server/BHS.CRG.Infrastructure
  --startup-project src/server/BHS.CRG.Api --context AppDbContext`. Проверка — старт на пересозданной
  базе применяет миграцию; `\d domain_objects` показывает частичный индекс
- [ ] 1.2 Порт `IFieldSuggestionReader` и `Infrastructure/Persistence/FieldSuggestionReader.cs`: окно
  подзапросом (≤ 1000, ≤ 8 МБ по `pg_column_size`), поле — только у записей окна, `SET LOCAL
  statement_timeout = '300ms'` [CORE-34.4]. Проверка — интеграционный тест плана: у типа с 1 000
  записями с картинками вне окна EXPLAIN показывает `Limit` над `Index Scan` нового индекса и не
  показывает `Seq Scan on domain_objects`
- [ ] 1.3 Строка нового места чтения в `ArchiveReadInventoryTests` с решением «выбор» и живой тест в
  `ArchiveReadPurposeTests` [CORE-34.4]. Проверка — обе переписи зелёные; тест кладёт запись в архив и не
  видит её значения

## 2. Правила ответа

- [ ] 2.1 `SuggestionSources.For` (чистая): объявивший тип по В-2, семья по `IsSameOrDescendant`, выпадение
  перекрывших и исключивших ключ, отбор по `DataAccess` (`id.document.read` + модуль `id`;
  `core.catalog.read`; по В-1 — `core.nomenclature.edit` для семьи `role.nomenclature`), «тип строки
  массива — пусто» [AUTH-8]. Проверка — модульные тесты на каждую ветку
- [ ] 2.2 `SuggestionRanking` (чистая): нормализация, совпадение с начала слова, склейка, равное `q`
  выбрасывается, частота по записям, порог 1/2, своя стройка первой, уровень «вся система» по В-3.
  Проверка — модульные тесты по сценариям спеки
- [ ] 2.3 `FieldSuggestionsQuery`: `Suggestable` (не подсказывается — 200 и пусто), неизвестный тип/поле
  — 400, `_baseRef` не разрешается, своя стройка по `scope`/`scopeId` через `ScopeCascade`, таймаут
  `57014` — 503. Проверка — интеграционные тесты: порог, своя стройка первой, архив, `_baseRef`,
  перекрытие ключа потомком, 503 на таймауте (искусственно малый предел)

## 3. Адрес и ворота

- [ ] 3.1 Адрес в `Api/Endpoints/Core/FieldSuggestionEndpoints.cs` под `RequireAuthorization()`, в
  `EndpointMap.cs`; строка в `GatedInside` с названным сторожем [AUTH-9]. Проверка —
  `EndpointGateInventoryTests` зелёный; без строки — красный с названием адреса
- [ ] 3.2 Права в службе [AUTH-8]. Проверка — интеграционные тесты: без `id.document.read` значение из
  документа не приходит; модуль `id` выключен — документы не участвуют; без обоих прав — 403
- [ ] 3.3 Ответ на проводе — только `{ value, here }` [CORE-34.4]. Проверка — тест читает сырой JSON
  ответа и сверяет набор ключей пунктов; `curl` по полю с `org.inn` или `input.noSuggest` — 200 и пусто;
  `curl` с типом строки массива — 200 и пусто
- [ ] 3.4 Две роли на стенде: без `id.document.read` значение, набранное только в документе, не
  приходит. Проверка — описание PR с запросами и ответами

## 4. Замер и документы

- [ ] 4.1 Замер p95 на синтетике по образцу #1528 (50k записей, картинки по 300 КБ) с индексом и окном.
  Проверка — числа в описании PR; p95 > 150 мс — сказать: это условие перехода на производную таблицу
- [ ] 4.2 `CLAUDE.md` и `AGENTS.md` тем же коммитом: адрес — в «REST API» (200-пусто против 503,
  подсказки не зовутся на фокус); подсказки — в «Условие пересмотра» инварианта `CatalogScope`.
  Проверка — `AgentRulesCopyTests` зелёный
- [ ] 4.3 `docs/DEV_NOTES.md` — адрес в листинге REST API; `docs/tz/90-этапы.md` — место отбора по
  правам в перечне `STG-11` (видимость типов, `TYPE-5`). Проверка — ревью PR

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FieldSuggestion|FullyQualifiedName~Suggestion|FullyQualifiedName~ArchiveRead|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~AgentRulesCopy"` — зелёный
- [ ] 5.2 `bash scripts/backend-tests.sh` — весь набор двумя потоками зелёный (новая миграция)
- [ ] 5.3 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [ ] 5.4 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.5 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
