# Tasks: правило `Suggestable` и тэг `input.noSuggest`

PR закрывает #1529 («Closes #1529»), метка `version:minor`. `<Version>` не трогать.

## 0. До кода

- [x] 0.1 Решение В-4 зонтика (22.10.2026: (а) — оба тэга однозначны, коды `article` и `orgName`) (`nomenclature.article`, `org.name`) записано в `../suggest-00-epic/design.md`
  с пометкой «Решено». Проверка — пометка есть

## 1. Тэг

- [x] 1.1 `FunctionalTag.InputNoSuggest = "input.noSuggest"` и запись в `TagRegistry.cs`: подпись,
  описание, `TagScope.Field`, виды `["string"]`, `Multiple: true`, владелец — ядро [TYPE-21] [TYPE-22].
  Проверка — тест реестра: `GET /api/tags` (`TagCatalog.All`) содержит тэг с описанием, видом `string`,
  кратностью и владельцем ядро; редактор не предлагает тэг полю `text` (отбор по `appliesTo`), а
  попавший туда в обход редактора ничего не меняет — случай общего файла «вид раньше всего» (см.
  design.md, «Вид поля под тэгом»)
- [x] 1.2 Код тэга — в `FUNCTIONAL_TAG` (`shared/api/tags.ts`); случай поля с `input.noSuggest` — в
  `schemaRoundTrip.test.ts` [TYPE-2]. Проверка — тест круга зелёный, тэг на месте после
  `parseSchemaFields` → `schemaToJson`

## 2. Правило

- [x] 2.1 Общий файл случаев `src/client/src/shared/api/suggestableCases.json`: случай на каждый код
  (`type` для `text`, `primitive`, `boolean`; `computed`; `locked`; `identity` с номером; `inn`;
  `docNumber`; `personnelNumber`; `article`; `orgName`; `manual`; «подсказывается»), случай порядка проверок [TYPE-2].
  Проверка — файл читают оба теста ниже
- [x] 2.2 `Application/Objects/FieldSuggestions/Suggestable.cs` — код причины по виду, `Computed`,
  `Locked` и тэгам (по коду тэга, без номера) и тест `Tests/Schema/SuggestableCasesTests.cs` на общем файле
  [TYPE-2]. Проверка — `dotnet test --filter SuggestableCases` зелёный; поломка порядка в C# его роняет
- [x] 2.3 `shared/api/suggestable.ts` и `suggestable.test.ts` на том же файле [TYPE-2]. Проверка —
  `npm test -- suggestable` зелёный; поломка порядка в TS его роняет
- [x] 2.4 Перепись строковых тэгов поля ядра: тест перечисляет тэги реестра с видом `string` и требует
  для каждого решение «однозначный (код) / нет» [TYPE-2]. Проверка — новый строковый тэг без решения
  роняет тест с названием тэга

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Suggestable|FullyQualifiedName~TagRegistry|FullyQualifiedName~Tags"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Перед архивацией: требование «Действующие тэги ядра — поля записей» правит и `core-org-short-name` (тэг `org.shortName`). Кто архивируется вторым, сверяет MODIFIED с действующей спекой и переносит оба тэга, иначе архивация затрёт первый
