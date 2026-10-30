# Tasks: свои чипы, пометки и вкладки экранов — на `Chip`, `StatusBadge`, `Tabs`

PR с «Closes #1416» (последний из порций). Перевод без новой функциональности — PATCH; PR с вариантом
`count` у `StatusBadge` (В1 — А) — метка `version:minor`.

## 0. До кода

- [x] 0.1 (22.10.2026: А — вариант `count` у `StatusBadge`) Решение владельца по В1 (счётчик: А — вариант `StatusBadge`, Б — `Chip count`, В — свой
  компонент) — до перевода счётчиков (задача 4). Если ответ — новый вид компонента, разбор с Дизайнером
  и Архитектором (затронут `shared/ui`) и, по слову владельца, макеты. Проверка — ответ записан в
  `design.md` (D4) и в issue
- [x] 0.2 (31.10.2026) Сверка под D8 зонтика `sharedui-md3-00-epic` (его задача 0.3): дельты
  [UI-42]…[UI-45] формы не называют — пометка общая (`StatusBadge`, с 12 — `StatusText`), чип — форма
  компонента (8 px, вид — 10), у пометок нет `rounded-full`; 16 мест формы чипа из `sharedui-md3-06`
  разобраны (D7), выбор темы заменён выбором языка (D2). Проверка — `openspec validate
  sharedui-chip-badge-tabs-screens --strict`

## 1. Чипы и сегменты

- [ ] 1.1 Сегменты режима → `Tabs variant="segmented"`: `costs/SiteCostsPage.tsx:303`,
  `settings/PrimitiveTypesPage.tsx:191,247` [UI-42] [UI-44]. Проверка — живые прогоны `site-costs` и
  `types` (селекторы — по роли `tab`) зелёные; стрелки двигают фокус, Enter переключает
- [ ] 1.2 Чипы → `Chip`: `costs/InvoiceQueueChips` (`count`), `costs/SupplierMatchesDialog`,
  `document-sets/SetProblemsPanel`, `document-sets/editor/MaterialsTable`,
  `reconciliations/ObservationsPanel`, `reconciliations/ReconciliationEditor`,
  `reconciliations/ReconciliationsPage`, `settings/InheritedFieldsPanel`, `settings/SchemaEditor`
  [UI-42]. Проверка — `npm run ui:ratchet` опускает `chip`; прогоны `invoices`, `table-chips` зелёные
- [ ] 1.3 Выбор языка `account/AppearanceSection` — `radiogroup` / `radio` + `aria-checked`, стрелки
  (D2) [UI-44]. Проверка — тест: стрелка выбирает соседний язык; `chip` по файлу — 0
- [ ] 1.4 Кнопки в форме чипа (D7) [UI-42]:
  - действия → `Button variant="outlined"`: `datasets/PdfGroupingEditor` («Отделить», «Перенести в
    группу», «Таблица», «Документ»), `datasets/SourcePreviewDialog`, `document-sets/SubscribersResource`,
    `document-sets/editor/GenerationTab` (3), `document-sets/editor/QualityLinksTab`,
    `quality-docs/QualityDocsPage`, `settings/EmailSettingsSection`;
  - отбор подозрительных страниц `PdfGroupingEditor` → `Chip selected tone="warning"`;
  - `tables/ColumnsPanel`, `tables/ExportMenu` → `Chip menu`;
  - триггер типа поля `settings/FieldBuilder` — поле во всю ширину ячейки.
  Проверка — `chip` по этим файлам — 0; прогоны `table-view` (кнопки «Колонки…», «Выгрузить» по имени)
  и `table-bridge` зелёные

## 2. Вкладки без ролей

- [ ] 2.1 `document-sets/editor/LinkPickerModal`: «Выбрать / Найти / Создать» → `Tabs` + `TabPanel`;
  переход на «Найти» по-прежнему зовёт `enterSearch` [UI-44]. Проверка — живой прогон `dialogs` (выбор
  ссылки) зелёный; тест — Enter на «Найти» открывает поиск

## 3. Пометки

- [ ] 3.1 Пометки → `StatusBadge`, тон по D5: `costs/WaybillForm`, `datasets/ProcessingTemplatesDialog`
  (4 пометки, не счётчики), `document-sets/ConstructionsList`, `document-sets/PlanProgressBadge`,
  `document-sets/SetProblemsPanel` (2), `document-sets/catalog/BindingCheckReport`,
  `document-sets/catalog/CatalogResource` (3), `document-sets/editor/DocumentObservations` (2),
  `document-sets/editor/index` (статус документа), `reconciliations/ReconciliationsPage` (2),
  `settings/FieldBuilder` (2), `settings/GroupedFieldsEditor`, `settings/PrimitiveTypesPage` (2),
  `settings/SchemaEditor` [UI-43]. Экран передаёт тон, подпись и значок, а не классы вида (D8).
  Проверка — `npm run ui:ratchet` опускает `badge`; перечень
  «пометка → тон» в описании PR
- [ ] 3.2 `FunctionalTagChip` остаётся в уровне `badge`; `docs/UI.md`, «Кто сторожит», называет его
  законным остатком (#630) [UI-43]. Проверка — ревью PR

## 4. Счётчик (после 0.1)

- [ ] 4.1 Вариант `count` у `StatusBadge` (В1 — А) и перевод счётчиков: `datasets/ProcessingTemplatesDialog.tsx:142–150` и
  `shared/ui/ListDetailShell.tsx:138,160`; строка «Счётчик» в таблице контролов `docs/UI.md` [UI-45].
  Проверка — `badge` по `ProcessingTemplatesDialog` — 0; тест компонента счётчика (число, тон)

## 5. Верификация

- [ ] 5.1 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.2 `cd src/client && npm test` — зелёный
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста; `chip` 0 (было 28), `badge` 1 (было 30), `tabs` 0
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
  (уровень `PrimitiveTypesPage` опущен по факту)
