# Proposal: ручные меню на страницах — на Radix

Самостоятельный change общих компонентов интерфейса (домен `sharedui`). Идёт после
`sharedui-dropdown-layer-guard` (#1537): правило храповика `manual-dropdown` заводит он, этот опускает
его уровень до нуля.

## Why

Issue #1538 (три ручных меню на страницах — на Radix: редактор шаблона, разбиение PDF). Меню были
сделаны вручную — `absolute` + `z-*`, без портала и без клавиатуры меню:

| Где | Что |
|---|---|
| `features/templates/EditorPanel.tsx:71` | «Реквизит»: поля типа для вставки `#get(…)` |
| ~~`features/templates/EditorPanel.tsx:378`~~ | стрелка у «Сохранить» — **уже сделано**: `SplitButton` (#1696) |
| `features/datasets/PdfGroupingEditor.tsx:214` | «Перенести в группу» в полосе выделенных страниц |

В окнах они не стоят, но обрезка грозит и им: редактор шаблона лежит в `flex-1 overflow-hidden`, и
«Реквизит» (`max-h-80`) у невысокого окна браузера уходит под край; «Перенести в группу» стоит в
прокручиваемой ленте групп и у нижней группы раздвигает прокрутку, к тому же с `z-10` и тенью мимо
шкалы. Ни одно не закрывается по Esc, стрелками по пунктам не пройти; «Перенести в группу» не
закрывается и щелчком мимо.

## What Changes

- «Перенести в группу» → Radix `DropdownMenu`, как `RowActionsMenu`. Меню «Сохранить» здесь уже нет —
  его закрыл `SplitButton` (#1696, сверка 30.10.2026).
- «Реквизит» → Radix `DropdownMenu`: дерево полей с отступом по глубине; составные строки — заголовки
  групп, а не пункты (сегодня — `pointer-events-none`).
- Портал в body, вид слоя `MENU_SURFACE` (`bg-surface` / `border-outline-variant` / `shadow-elevation-2`,
  `docs/UI.md`, «Слои над окном»);
  Esc и щелчок мимо закрывают меню, стрелки ходят по пунктам.
- Свой слушатель `mousedown` на `document` в `RequisitePicker` уходит — закрытие по щелчку мимо делает Radix.
- Уровень правила `manual-dropdown` — ноль.

## Capabilities

### New Capabilities
- `sharedui`: общие компоненты интерфейса — `shared/ui`, оболочка, храповик интерфейса и правила
  `docs/UI.md`.

### Modified Capabilities
- Нет.

## Impact

- **Клиент:** `features/templates/EditorPanel.tsx`, `features/datasets/PdfGroupingEditor.tsx` (в уровне
  размера, 540 строк — только худеет).
- **Уровни:** `src/client/ui-baseline.json` (`manual-dropdown` → пусто).
- **Живые прогоны:** ни один не открывает эти три меню — сторож здесь тесты компонентов.
- **Сервер:** не меняется.
