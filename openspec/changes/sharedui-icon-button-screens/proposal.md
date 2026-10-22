# Proposal: кнопки из одной иконки — на `IconButton`

Самостоятельный change общих компонентов интерфейса (домен `sharedui`). Правило храповика уже слито,
остался перевод экранов.

## Why

Issue #1417 (кнопки из одной иконки → `IconButton`). В `features/` нативные `<button>`, единственное
содержимое которых — значок, стоят в 89 местах 49 файлов (`src/client/ui-baseline.json`,
`icon-button`; правило слито PR #1419). Это самодельный `IconButton`: размер у каждого свой (`p-1`,
`p-1.5`, `w-9 h-9`), и правило «кнопка-иконка не меньше 32 px» (`docs/UI.md`, «Узкий экран») на них
не действует; имя для скринридера держится на памяти автора; подсветка наведения своя.

## What Changes

- Перевод 89 мест на `IconButton` (`shared/ui/Button.tsx`): `label` — имя для скринридера и подсказка,
  `size` `sm` | `md` | `lg` — 32 / 36 / 40 px, `danger` для удаления, своя подсказка — проп `title`.
- Внешний `<Tooltip>` вокруг такой кнопки снимается: его текст уходит в `title` (у `IconButton`
  подсказка своя, второй слой дал бы две).
- Триггеры Radix (`Popover.Trigger asChild`, `DropdownMenu.Trigger asChild`) получают `IconButton` —
  он передаёт ref.
- Живые прогоны, искавшие такие кнопки по классу или порядку, переходят на имя.
- Уровень `icon-button` опускается порциями, тем же PR; итог — ноль.

## Capabilities

### New Capabilities
- `sharedui`: общие компоненты интерфейса — `shared/ui`, оболочка, храповик интерфейса и правила
  `docs/UI.md`.

### Modified Capabilities
- Нет.

## Impact

- **Клиент:** 49 файлов `features/`; больше всего — `document-sets/fields/ComplexFields.tsx` (11),
  `document-sets/fields/DocRefField.tsx` (7), `costs/ArticlesDialog.tsx` (6),
  `datasets/PdfGroupingEditor.tsx`, `settings/FieldBuilder.tsx`, `templates/TemplateSidebar.tsx` (по 3).
- **Уровни:** `src/client/ui-baseline.json` (`icon-button`), `src/file-size-baseline.json`
  (`ComplexFields`, `PdfGroupingEditor`, `FieldBuilder` — по факту).
- **Соседние change:** `FieldBuilder` правит и `sharedui-dropdown-layer-guard` (#1537),
  `PdfGroupingEditor` и `EditorPanel` — `sharedui-page-menus-radix` (#1538): порядок — в `design.md`.
- **Сервер:** не меняется.
