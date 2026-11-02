# Proposal: строки списков list-detail по MD3 — общий `ListItem`

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D6, D7, D8, D10, D11, D12). Вид выбран владельцем 02.11.2026 в `sharedui-md3-14-data` — вариант А «Data
table буквально», экран 5 «Список list-detail» (макеты
https://claude.ai/design/p/2d7f81d4-5708-4674-87a1-fac3ddc04e33). Тем же ответом списки вынесены из 14
сюда, по совету Дизайнера и Архитектора.

## Why

Строка списка — то, что пользователь видит в левой колонке каждой страницы list-detail: типы документов,
счета, накладные, наборы данных, роли, профили, разделы уровня. Общей строки нет. `NavItem` в
`ListDetailShell` — пилюля 44 на `primary-container`, а около двадцати строк экраны рисуют сами, и каждая
по-своему:
- форма — пилюля (`DocumentTypesPage`, раздел реквизитов), скругление 6–8 (`RolesPage`, `BugReportsPage`,
  `DataSetsResource`), прямоугольник с чертой (`InvoiceListRow`, `WaybillsPage`);
- высота — от 36 до 44, с отступом `py-1.5` или `py-2`;
- выбранная — везде `primary-container`, а по MD3 — `secondary-container`.

Правка `ListDetailShell` до этих строк не доедет (D9 зонтика): нужен общий компонент и сторож, который не
даст нарисовать строку руками снова.

## What Changes

- **`ListItem`** в `shared/ui` — list item MD3 по варианту А:
  - прямоугольник во всю ширину колонки, без скругления, отступы 16;
  - в одну строку — 48, в две — 64; подпись `body-large` роли `on-surface`, вторая строка — `body-medium`
    роли `on-surface-variant`;
  - ведущий слот (значок, миниатюра, флажок), замыкающий — пометка, число (`CountText`), шеврон или
    действие строки;
  - выбранная — `secondary-container` и `on-secondary-container`, наведение и фокус — слоем состояния (D6);
  - роль у строки одна, её выбирает место: пункт, меняющий карточку (`aria-current`), переход вглубь
    (шеврон, ссылка) или отбор списка (`filter`).
- **`NavItem`** становится тонкой обёрткой над `ListItem`: его поведение на узком месте (`showDetail`,
  `filter`, шеврон «ребёнка») сохраняется, вид — новый.
- **Заголовок группы** `NavSection` — `title-small` роли `on-surface-variant` с отступами 16 / 16 / 8, фон
  колонки — `surface` (как в макете А).
- **Строки экранов** переходят на `ListItem`: 14 строк в 13 файлах `features/`, ещё два действия «Новый
  раздел» в колонке становятся кнопкой `text`, а деревья шаблонов — по ответу 0.3 (перечень — `design.md`,
  «Перечень»).
- **Сторож** — правило храповика `list-row` (своим модулем, `ui-ratchet-lib.mjs` стоит у порога размера):
  строка с условной заливкой выбранного и слоем состояния, нарисованная в `features/`.
- `docs/UI.md` — строка «Строка списка» в таблице «Язык формы», раздел «Списки list-detail».
- **Решения владельца, нужные до кода** (задача 0.3): длина подписи `body-large` в колонке 320; высота
  строки в одну строку; деревья шаблонов; списки выбора в окнах; размер ведущего значка. Новые три макета —
  только если владелец не примет ответ без них.
- **Не входит:**
  - пункты подпанели (`panelLink`, `PageList`, `WorkspaceNav`) — drawer, их делает `sharedui-md3-15-shell`;
  - строки таблиц — `sharedui-md3-14-data`;
  - списки выбора в окнах (`role="option"`) и списки содержимого карточки (замечания сверки,
    уведомления, элементы массива) — по ответу 0.3, по умолчанию `sharedui-md3-17-screens`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: строка списка list-detail по MD3 и сторож самодельных строк [UI-84].

## Impact

- **Клиент:** новый `shared/ui/ListItem.tsx`; `ListDetailShell.tsx` (`NavItem`, `NavSection`, фон колонки).
  Экраны: `costs/InvoiceListRow.tsx`, `costs/WaybillsPage.tsx`, `datasets/DataSetsResource.tsx`,
  `document-sets/catalog/CatalogResource.tsx`, `document-sets/ConstructionDetail.tsx`,
  `document-sets/SectionDetail.tsx`, `document-sets/editor/RequisitesTab.tsx`,
  `document-sets/editor/GenerationTab.tsx`, `settings/DocumentTypesPage.tsx`,
  `settings/PrimitiveTypesPage.tsx`, `settings/PrintProfilesPage.tsx`,
  `settings/RecognitionProfilesPage.tsx`, `settings/RolesPage.tsx`, `settings/BugReportsPage.tsx`,
  `templates/TypeBlocksPanel.tsx`; по ответу 0.3 — `templates/UserLibFileList.tsx`,
  `templates/TemplateSidebar.tsx`.
- **Сторожа:** новое правило `list-row` в своём модуле `scripts/ui-ratchet-lists.mjs` с тестом; перечень
  правил в `CLAUDE.md` и `AGENTS.md` — одним коммитом.
- **Тесты:** `listDetailView.test.tsx`, `primitives.test.tsx` (`NavItem`), новый `ListItem.test.tsx`.
- **Живые прогоны:** весь список (`ListDetailShell` стоит на двадцати экранах); новые проверки высоты и
  фона выбранной строки — в `pages-smoke` или `routing-smoke`.
- **Claude Design:** `/design-sync` после слияния.
- **Сервер:** не меняется.
- **PR:** два — `ListItem`, `NavItem` и правило; строки экранов. Оба с меткой `version:minor`.
