# Tasks: флажки экранов — на `Checkbox`

PR с «Closes #1414». Перевод без новой функциональности — PATCH, метка `version:minor` не нужна.

## 1. Сам `Checkbox`

- [ ] 1.1 Подпись `Checkbox` — блок `flex w-fit` вместо `inline-flex` [UI-41]. Проверка — тест
  `Checkbox`: две подписи подряд стоят двумя блоками, щелчок по подписи переключает свой флажок

## 2. Настройки

- [ ] 2.1 `features/settings/`: `CreateForm`, `EmailSettingsSection` (2), `FieldBuilder` (3),
  `IntegrationSettingsSection`, `PrimitiveTypesPage`, `ProxyToggle`, `RecognitionProfilesPage`,
  `RolesPage`, `UpdateSettingsSection`; `features/account/WorkspacesDialog` (3) — строка своей разметкой,
  `Checkbox` без `label` [UI-40] [UI-41]. Проверка — `npm run ui:ratchet` опускает уровень,
  живые прогоны `routing` (матрица прав), `settings` (движки, рабочие пространства), `types`, `fields`
  зелёные
- [ ] 2.2 Уровень размера: `FieldBuilder` и `PrimitiveTypesPage` в `src/file-size-baseline.json` — по
  факту. Проверка — `dotnet test … --filter FileSizeRatchet` зелёный

## 3. Документы, наборы, справочники

- [ ] 3.1 `features/document-sets/`: `EmailSendDialog`, `catalog/PickRow` (без `label` в строке),
  `editor/DocumentTemplateParams` (`aria-label`), `editor/FieldSourceBinding`, `editor/GenerationTab`,
  `editor/LinkPickerModal`, `editor/SuggestionsModal`, `fields/ComplexCells` (`size="sm"`, `aria-label`),
  `fields/ComplexFields` (`size="sm"`), `fields/PasteArchivedNote`, `fields/PasteMappingModal`,
  `fields/PrimitiveInput` (только чтение, `aria-readonly`) [UI-40]. Проверка — живые прогоны `dialogs`
  (вкладка генерации) и `fields` зелёные; флажок только для чтения не меняется по щелчку
- [ ] 3.2 `features/datasets/PdfSourceDialog`, `features/quality-docs/QualityDocLinks`,
  `features/reconciliations/ReconciliationsPage`, `features/costs/WaybillsPage` [UI-40]. Проверка —
  `npm run ui:ratchet` опускает уровень

## 4. Таблицы и шаблоны

- [ ] 4.1 `features/tables/ColumnsPanel` (строка своей разметкой), `features/tables/SaveViewDialog`,
  `features/templates/TemplateParamsPanel` (под `Tooltip`, `aria-label`), `features/templates/TemplateSidebar`
  [UI-40]. Проверка — живые прогоны `table-view` (панель колонок) и `table-saved-views` («Общее») зелёные

## 5. Правила

- [ ] 5.1 Уровень `native-checkbox` — ноль (`npm run ui:ratchet:update`, `ui-baseline.json` в коммите);
  `docs/UI.md`, «Кто сторожит»: накопленных нет, уровень — ноль [UI-40]. Проверка — ревью PR;
  `grep -rn 'type="checkbox"' src/client/src/features` пуст

## 6. Верификация

- [ ] 6.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 6.2 `cd src/client && npm test` — зелёный
- [ ] 6.3 `cd src/client && npm run ui:ratchet` — без роста, `native-checkbox` 0
- [ ] 6.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
