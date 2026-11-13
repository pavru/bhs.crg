# Tasks: дверь видов работ отличает «не заведён» от «пусто» (issue #1936)

## 1. Сервер

- [x] 1.1 `WorkTypeEndpoints.DirectoryMissingResult(all)`. Он возвращает `409 { code, reason, error }`.
  - Код — `EmployeeAccountReads.DirectoryMissing`.
  - Причина: `unit-missing`, если нет типа `CoreRecordTypes.UnitCode`, иначе `not-created`.
  - Слова: для каждой причины — своя причина и свой выход.

  Метки [WORK-59] [CORE-20.4] (D1).
- [x] 1.2 Ветка списка `GET /api/work-types` и ветка заведения `POST /api/work-types` зовут
  `DirectoryMissingResult` вместо `Ok(пусто)` и `Conflict(new { error })`. Порядок — право → `purpose` →
  справочник [WORK-59] [CORE-20.4] (D2).
- [x] 1.3 `WorkTypeEndpointsTests`, метки [WORK-59] [CORE-20.4]:
  - без справочника единиц: список — 409 `directory-missing` / `unit-missing`, заведение — то же тело,
    запись не заведена;
  - справочник единиц есть, типа `ВидРаботы` нет: 409 / `not-created`;
  - пустой классификатор: 200 и `[]`;
  - без `core.catalog.read`: 403, а не 409;
  - без `purpose`: 400.

## 2. Клиент

- [x] 2.1 `shared/api/directoryMissing.ts`: `directoryMissingOf(e) → { reason: string; error: string } | null`.
  `employees.ts` сужает `reason` поверх него, поведение не меняется (D3). Проверка — vitest на разбор:
  - 409 с кодом — значение;
  - 409 без кода — `null`;
  - другой статус — `null`.
- [x] 2.2 `useWorkTypeChoices` возвращает `{ items, missing }`, `useWorkTypeNames` — `{ names, missing }`.
  `directory-missing` — значение, а не исключение; потребители — `useRateChoice`, `RevisionLines`,
  `MatchDialog`, `MakeCurrentDialog` — переведены на новую форму [WORK-59] [CORE-20.4] (D3).
- [x] 2.3 `RateChoiceFields.tsx`:
  - при `missing` поле недоступно и показывает слова сервера без роли `alert`;
  - «Классификатор пуст» — только при пустом списке без `missing`.

  В `MatchDialog.tsx` при `missing` действия «Новый вид работы» нет в разметке [WORK-59] (D4).
  Проверка — vitest окна сопоставления:
  - при отказе `directory-missing` видны слова отказа;
  - нет «Классификатор пуст»;
  - нет «Новый вид работы» даже с `core.worktypes.edit`.

## 3. Документация

- [x] 3.1 `docs/DEV_NOTES.md`, листинг REST API у `GET` и `POST /api/work-types`: без классификатора —
  `409 { code: "directory-missing", reason: "unit-missing" | "not-created", error }`. Пустой — 200 и `[]`.

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkTypeEndpointsTests|FullyQualifiedName~EmployeeDirectoryTests|FullyQualifiedName~EndpointGateInventoryTests"`
  с `BHS_TEST_DB` своей базы. Затем весь набор — `bash scripts/backend-tests.sh`.
- [x] 4.2 `cd src/client && npx tsc -b`, `npm test`, `npm run lint:ratchet`, `npm run ui:ratchet`.
- [x] 4.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet`.
- [x] 4.4 `openspec validate --all --strict --no-interactive`.

## Workflow follow-up

- PR в master без `version:minor` (исправление); в описании — `Closes #1936`.
- После слияния — `/opsx:archive core-work-types-not-set-up` отдельным PR: требование уходит в
  `openspec/specs/core/spec.md`.
