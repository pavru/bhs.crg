# Proposal: таблица данных, сетка правки и списки по MD3

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D6, D7, D8, D10, D11, D12).

## Why

Таблицы — главный рабочий вид приложения: реестры, строки счёта, матрица разноски, наборы данных. И
это самое плотное место, где Fluent держится дольше всего:
- `DataGrid` — карточка `rounded-xl` с рамкой, шапка 42 на заливке `bg-muted` текстом 12/500 `fg3`,
  строка около 36;
- в `compact` — шапка 32 и строка около 26, ниже любой плотности MD3;
- выбранная строка — `brand-subtle`, наведение — `black/[.03]`;
- `EditGrid` — клетка `py-1`, а у `sheet` строка 28 с линиями колонок.

В M3 таблицы данных нет. Data table — расширение из Material 2 на токенах MD3 (D8). Вариант Г
задал её вид: шапка 56 без заливки, строка 48, поле в клетке 40 без рамки. Таблица плотности D7 делает
его правилом: страница −1, окно и боковой лист −2.

Списки list-detail и подпанели — плотные строки `py-1.5–2`. По MD3 это списки 48–52 с выбранной строкой
`secondary-container`.

## What Changes

- **`DataGrid`:**
  - плотность −1 на странице: шапка 56, строка 48;
  - `compact` — плотность −2 в окне и боковом листе: шапка 48, строка 40;
  - шапка без заливки, подпись `title-small` роли `on-surface`, черта под шапкой и между строками —
    `outline-variant`;
  - выбранная (открытая) строка — `secondary-container`, наведение — слоем состояния (D6);
  - обёртка-карточка — outlined card 12;
  - итог внизу — `surface-container`.
- **`EditGrid`:** строка 48, поле в клетке 40 без рамки, у фокуса — подчёркивание `primary`. `sheet`
  (строки массива документа) — по решению В5: рекомендация «ниже −2 не оставлять» даёт ту же −2,
  линии колонок остаются.
- **Пометки строк** (`marked` — тон предупреждения) переходят на `md-ext warning-container`.
  Закреплённые колонки берут фон своей строки.
- **Списки.** Строки list-detail (`ListDetailShell`, `PageList`, ссылки `panelLink`) — плотность −1…−2,
  48–52. Выбранная — `secondary-container`, подпись `label-large` / `body-large`, вторая строка —
  `body-medium` роли `on-surface-variant`.
- **Правки e2e,** проверяющих высоты строк и шапок (`invoice-layout-checks`, `allocation-matrix-smoke`,
  `fields-smoke`, `table-*` и др.), — тем же PR, что меняет компонент.
- `docs/UI.md` «Таблицы» и «Плотность» — таблица D7 словами MD3, data table — в разделе отступлений.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: таблица данных, сетка правки и списки по шкале плотности MD3 [UI-78].

## Impact

- **Клиент:** `shared/ui/DataGrid.tsx`, `EditGrid.tsx`, `dataTable.ts` (классы `dt*` — один источник),
  `dataGridStates.ts`, `ListDetailShell.tsx`, `PageList.tsx`, `panelLink.ts`. Экраны с `DataGrid` (32
  файла) и `EditGrid` (5) получают вид сами. Свои колонки ширины (`fixed-width`) перепроверяются: колонки
  действий растут с кнопками-иконками 40 из `sharedui-md3-10-actions`.
- **Сторожа:** новых правил нет. Тесты `DataGrid.test.tsx`, `EditGrid.test.tsx`, `dataGridColumns.test.ts`
  — ожидания классов переписываются вручную, вместе с отрицательными (D11).
- **Живые прогоны:** весь список; размеры меряют 25 файлов, из них таблицы — `invoice-layout-checks`,
  `allocation-matrix-smoke`, `table-view-smoke`, `table-chips-smoke`, `invoice-lines-smoke`.
- **Claude Design:** `/design-sync` после слияния.
- **Сервер:** не меняется.
- **PR:** два — `DataGrid` и списки; `EditGrid`. Оба с меткой `version:minor`.
