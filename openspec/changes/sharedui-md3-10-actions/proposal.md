# Proposal: действия по MD3 — кнопки, кнопки-иконки, чипы, segmented button, FAB

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D7, D8, D9, D10, D11).

## Why

Кнопки — главный хвост Fluent: из 449 `<Button>` и 138 `<IconButton>` 335 стоят на `size="sm"`
32 px, по умолчанию 36, а поля рядом — 56. По MD3 кнопка одна, 40 px (D7: кнопкам плотность не применяется).

- **`IconButton`** — 32/36/40 со знаком 13–16 px. Вариантов MD3 (filled, tonal, outlined) и
  переключения у неё нет.
- **`Chip`** — пилюля ~24 px с текстом 12. Выбранный чип — в цвете бренда с обводкой, а у MD3 —
  `secondary-container` с галочкой. Тоны `warning`/`danger` и пунктир в MD3 не существуют.
- **Сегментные вкладки** нарисованы «pivot» Fluent: подложка и белая плитка с тенью. У MD3 это segmented
  button.
- **`PrimaryFab`** стоит на `secondary-container`, а у extended FAB MD3 — `primary-container`.

Вариант Г, выбранный владельцем, рисует все пять по MD3. Судьбу `sm` (В7) владелец решил по трём макетам
31.10.2026 — вариант (Б).

## What Changes

- **`Button`** — 40 px, label-large, иконка 18. Четыре вида: filled без тени, tonal, outlined, text.
  - Высота одна, 40 везде, в панелях тоже: `size="sm"` и `"lg"` уходят (В7 = Б, 384 места с тремя за
    `ModalExit`). Кодмод на синтаксическом дереве, отдельным коммитом. Split-кнопка ([UI-83]) получает ту
    же высоту через общие классы.
  - `danger` и многострочная кнопка остаются как названные отступления (D8).
- **`IconButton`** — 40 px, знак 24, на телефоне цель нажатия 48.
  - В клетках `DataGrid` и `EditGrid` при мыши — 32 со знаком 20 (размер XS шкалы M3 Expressive, В7 = Б).
    Размер ставит сетка CSS-переменной, при касании — 40.
  - Варианты standard, filled, tonal, outlined.
  - Знак — свойством `icon`, кнопка рисует его сама; `children` уходит.
  - Переключатель `selected`: `aria-pressed` и залитый знак.
  - Самодельные кнопки `shared/ui` — «три точки» `RowActionsMenu`, крестик `EditGrid`, `MoveButtons` —
    переходят на неё.
- **`Chip`** — 32 px, форма 8 px, label-large. Три вида MD3:
  - filter — выбранный на `secondary-container` с галочкой, без обводки;
  - input — с крестиком снятия;
  - assist — с ведущим знаком, вместо пунктира «место под условие».

  `tone` уходит (12 мест): состояние называет ведущий знак цвета роли, и места без знака его получают.
  Вид выводится из свойств, тип запрещает бессмысленные сочетания. `role="radio"` уходит вместе с
  последним потребителем. `shared/filter/FilterChips` переходит на input chip.
- **Сегменты** — 40 px, форма full, контур, галочка у выбранного, `secondary-container`. Вид общий,
  компонента два:
  - `Tabs variant="segmented"` перерисован и остаётся для переключателя панели, клавиатура прежняя;
    разделы `TemplatesPage` — вкладки `underline`;
  - **`SegmentedButton`** (новый) — только выбор значения, группа радиокнопок. На него переходят места
    без панели и самодельные ряды: `ThemeToggle`, `VariantPicker`, `ChoiceRow`.

  Клавиатура вкладок и радиогрупп — один модуль.
- **`PrimaryFab`** — extended FAB по MD3: `primary-container`, форма 16, уровень 3.
- **`docs/UI.md`.** Таблица контролов переписана. Раздел «Отступления» заводится здесь: `danger`,
  многострочная кнопка, цель касания по основному указателю.
- **Сторожа.** Переменная размера — только в `dataTable.ts`; размер в `className` кнопки; самодельный
  ряд сегментов; правило `icon-button` смотрит и `shared/`.
- **Правки по месту.** Живые прогоны, проверяющие размеры, правятся вместе с компонентом.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: вид и поведение действий по MD3 [UI-68]. Требования архивированных
  `sharedui-chip-badge-tabs-screens` и `sharedui-icon-button-screens` правятся здесь же (`tasks.md`, 0.5):
  [UI-42] — чип без тонов, значение — `SegmentedButton`; [UI-44] — вкладки видов `underline` и
  `segmented`; [UI-46] — кнопка-иконка 40×40, в клетке — по сетке, правило смотрит и `shared/`.

## Impact

- **Клиент:**
  - `shared/ui/Button.tsx` (`Button`, `IconButton`), `buttonClasses.ts` (с ним `SplitButton`),
    `dataTable.ts`, `RowActionsMenu`, `EditGrid`, `MoveButtons`, `Chip.tsx`, `Tabs.tsx`, `ThemeToggle`,
    новые `SegmentedButton.tsx`, `segmentedClasses.ts`, `rovingFocus.ts`, `PrimaryFab.tsx`,
    `shared/filter/FilterChips`, `index.css` (`touch-target`), генератор значков (заливка `Star`, `PushPin`);
  - места `size=` у кнопок — 384 (381 в 156 файлах и три за `ModalExit`); дети `IconButton` — 139 мест;
  - восемь мест `Tabs variant="segmented"` в шести файлах и три самодельных ряда;
  - `Chip tone|dashed` — 12 мест в 9 файлах;
  - кодмод `md3-size-migrate.mjs`, правила `ui-ratchet-lib.mjs`;
  - e2e с проверками размеров и ролей (`rail-checks`, `invoice-layout-checks`, `fields-smoke`,
    `allocation-matrix-smoke`, `site-costs-smoke`, `types-smoke`, `dialogs-smoke` и другие).
- **Документы:** `docs/UI.md`, `/design-sync` (D12).
- **Сервер:** не меняется.
- **PR:** два — (1) `Button`, `IconButton`, FAB; (2) `Chip`, `FilterChips`, сегменты. Оба с меткой
  `version:minor`.
