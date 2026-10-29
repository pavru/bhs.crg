# Tasks: кнопки из одной иконки — на `IconButton`

Порциями; последний PR — «Closes #1417». Перевод без новой функциональности — PATCH, метка
`version:minor` не нужна.

## 1. Поля документов

- [x] 1.1 `document-sets/fields/`: `ComplexFields` (11), `DocRefField` (7), `BaseCandidatePicker` (2),
  `DocRefCatalogPickerField` (2), `FileField` (2), `RefTile` (2), `ArrayTableModal`, `ComplexCells`,
  `ExtractToCommonDataModal` — `size="sm"` в строках [UI-46]. Проверка — живой прогон `fields` зелёный;
  `npm run ui:ratchet` опускает уровень; уровень размера `ComplexFields` — по факту
- [x] 1.2 `document-sets/editor/`: `DataSetsTab` (2), ~~`DocumentPreviewPanel` (2)~~ (переведён в `viewer-04-document-preview`), `MaterialsTable` (2),
  `ContainerFieldBinding`, `FieldSourceBinding`, `LinkPickerModal`, `index`; `document-sets/`:
  `PlanPanel`, `SetDetail`, `SubscribersResource`, `catalog/CatalogResource`, `catalog/index` [UI-46].
  Проверка — живой прогон `dialogs` зелёный

## 2. Счета

- [x] 2.1 `costs/`: `ArticlesDialog` (6), `AllocationMatrix`, `LineMatchChip`, `NomenclaturePicker`,
  `SupplierMatchesDialog`, `SupplierTermsDialog` [UI-46]. Проверка — живые прогоны `costs`,
  `allocation-matrix`, `invoice-candidates` зелёные

## 3. Наборы данных, таблицы, настройки, шаблоны

- [x] 3.1 `datasets/`: `ComputedColumnsDialog`, `ProcessingTemplatesDialog`, `RowFilterDialog`,
  `SortSpecDialog`, `SourceEditorDialog`, `SourcesExpander`, `xpath/XPathBuilder`; `tables/`:
  `TablePage` (2), `RowPanel` [UI-46]. Проверка — живые прогоны `table-view`, `table-chips` зелёные
- [x] 3.2 `settings/`: `BindingTemplatesDialog` (2), `FunctionalTagChip` (2), `GroupedFieldsEditor` (2),
  `EnumTypesSection`, `RecognitionProfilesPage`, `TypstRendersEditor`; `templates/`: `TemplateSidebar`
  (3), `UserLibFileList` (2), `TemplateParamsPanel`; `account/WorkspacesDialog` (2) [UI-46]. Проверка —
  живые прогоны `settings`, `types` зелёные
- [ ] 3.3 После `sharedui-dropdown-layer-guard` и `sharedui-page-menus-radix` (D4):
  `settings/FieldBuilder` (3), `datasets/PdfGroupingEditor` (3), `templates/EditorPanel` [UI-46].
  Проверка — уровни размера `FieldBuilder`, `PdfGroupingEditor` — по факту

## 4. Правила

- [ ] 4.1 Уровень `icon-button` — ноль (`npm run ui:ratchet:update`); `docs/UI.md`, «Язык формы» и
  «Кто сторожит»: накопленных кнопок-иконок нет [UI-46]. Проверка — ревью PR

## 5. Верификация

- [x] 5.1 `cd src/client && npx tsc -b` — без ошибок
- [x] 5.2 `cd src/client && npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста, `icon-button` 0
- [x] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
