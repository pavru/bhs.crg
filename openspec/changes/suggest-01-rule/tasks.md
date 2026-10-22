# Tasks: правило `Suggestable` и тэг `input.noSuggest`

PR закрывает #1529 («Closes #1529»), метка `version:minor`. `<Version>` не трогать.

## 0. До кода

- [ ] 0.1 Решение В-4 зонтика (`nomenclature.article`, `org.name`) записано в `../suggest-00-epic/design.md`
  с пометкой «Решено». Проверка — пометка есть

## 1. Тэг

- [ ] 1.1 `FunctionalTag.InputNoSuggest = "input.noSuggest"` и запись в `TagRegistry.cs`: подпись,
  описание, `TagScope.Field`, виды `["string"]`, `Multiple: true`, владелец — ядро [TYPE-21] [TYPE-22].
  Проверка — тест реестра: `GET /api/tags` содержит тэг с описанием; сохранение схемы с тэгом на поле
  `text` отклоняется
- [ ] 1.2 Код тэга — в `FUNCTIONAL_TAG` (`shared/api/tags.ts`); случай поля с `input.noSuggest` — в
  `schemaRoundTrip.test.ts` [TYPE-2]. Проверка — тест круга зелёный, тэг на месте после
  `parseSchemaFields` → `schemaToJson`

## 2. Правило

- [ ] 2.1 Общий файл случаев `src/client/src/shared/api/suggestableCases.json`: случай на каждый код
  (`type` для `text`, `primitive`, `boolean`; `computed`; `locked`; `identity` с номером; `inn`;
  `docNumber`; `personnelNumber`; `manual`; «подсказывается»), случай порядка проверок, итог В-4 [TYPE-2].
  Проверка — файл читают оба теста ниже
- [ ] 2.2 `Application/Objects/FieldSuggestions/Suggestable.cs` — код причины по виду, `Computed`,
  `Locked` и тэгам (по коду тэга, без номера) и тест `Tests/Schema/SuggestableCasesTests.cs` на общем файле
  [TYPE-2]. Проверка — `dotnet test --filter SuggestableCases` зелёный; поломка порядка в C# его роняет
- [ ] 2.3 `shared/api/suggestable.ts` и `suggestable.test.ts` на том же файле [TYPE-2]. Проверка —
  `npm test -- suggestable` зелёный; поломка порядка в TS его роняет
- [ ] 2.4 Перепись строковых тэгов поля ядра: тест перечисляет тэги реестра с видом `string` и требует
  для каждого решение «однозначный (код) / нет» [TYPE-2]. Проверка — новый строковый тэг без решения
  роняет тест с названием тэга

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Suggestable|FullyQualifiedName~TagRegistry|FullyQualifiedName~Tags"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Перед архивацией: требование «Действующие тэги ядра — поля записей» правит и `core-org-short-name` (тэг `org.shortName`). Кто архивируется вторым, сверяет MODIFIED с действующей спекой и переносит оба тэга, иначе архивация затрёт первый
