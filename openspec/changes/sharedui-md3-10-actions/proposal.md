# Proposal: действия по MD3 — кнопки, кнопки-иконки, чипы, segmented button, FAB

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D7, D8, D9, D10, D11).

## Why

Кнопки — главный хвост Fluent: больше половины из 420 `<Button>` стоят на `size="sm"` 32 px, по
умолчанию 36, а поля рядом — 56. По MD3 кнопка одна, 40 px (D7: кнопкам плотность не применяется).

- **`IconButton`** — 32/36/40 со знаком 13–16 px. Вариантов MD3 (filled, tonal, outlined) и
  переключения у неё нет.
- **`Chip`** — пилюля ~24 px с текстом 12. Выбранный чип — в цвете бренда с обводкой, а у MD3 —
  `secondary-container` с галочкой. Тоны `warning`/`danger` и пунктир в MD3 не существуют.
- **Сегментные вкладки** нарисованы «pivot» Fluent: подложка и белая плитка с тенью. У MD3 это segmented
  button.
- **`PrimaryFab`** стоит на `tonal` и тени окна.

Вариант Г, выбранный владельцем, рисует все пять по MD3. Владелец решает судьбу `sm` (В7).

## What Changes

- **`Button`** — 40 px, label-large, иконка 18. Четыре вида: filled без тени, tonal, outlined, text.
  - Высота одна: `size="sm"` и `"lg"` уходят (рекомендация В7, ≥ 242 места).
  - `danger` и многострочная кнопка остаются как названные отступления (D8).
- **`IconButton`** — 40 px, знак 24, на телефоне цель нажатия 48.
  - Варианты standard, filled, tonal, outlined.
  - Переключатель `selected`: `aria-pressed` и залитый знак.
- **`Chip`** — 32 px, форма 8 px, label-large. Три вида MD3:
  - filter — выбранный на `secondary-container` с галочкой, без обводки;
  - input — с крестиком снятия;
  - assist — с ведущим знаком, вместо пунктира «место под условие».

  `tone` уходит: состояние называет ведущий знак цвета роли. `shared/filter/FilterChips` переходит на
  input chip.
- **`SegmentedButton`** (новый) вместо `Tabs variant="segmented"` — 40 px, форма full, контур, галочка у
  выбранного, `secondary-container`. Клавиатура прежняя: у переключателя панели — вкладки с ручной
  активацией.
- **`PrimaryFab`** — extended FAB по MD3: `primary-container`, форма 16, уровень 3.
- **`docs/UI.md`.** Таблица контролов переписана. В разделе отступлений — `danger`, многострочная
  кнопка, цветной контейнер индикатора состояния (если его выберут в макетах).
- **Правки по месту.** Живые прогоны, проверяющие размеры, правятся вместе с компонентом.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: вид и поведение действий по MD3 [UI-68]. После архива `sharedui-chip-badge-tabs-screens`
  и `sharedui-icon-button-screens` их [UI-42] и [UI-46] правятся здесь же (`tasks.md`, 0.5).

## Impact

- **Клиент:**
  - `shared/ui/Button.tsx` (`Button`, `IconButton`), `Chip.tsx`, `Tabs.tsx`, новый
    `SegmentedButton.tsx`, `PrimaryFab.tsx`, `shared/filter/FilterChips`;
  - места `size=` у кнопок — 156 файлов;
  - три места `Tabs variant="segmented"`;
  - экраны с `Chip tone|dashed` — 12 файлов;
  - e2e с проверками размеров (`rail-checks`, `invoice-layout-checks`, `fields-smoke`,
    `allocation-matrix-smoke` и другие).
- **Документы:** `docs/UI.md`, `/design-sync` (D12).
- **Сервер:** не меняется.
- **PR:** два — (1) `Button`, `IconButton`, FAB; (2) `Chip`, `FilterChips`, `SegmentedButton`. Оба с
  меткой `version:minor`.
