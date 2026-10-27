# Proposal: окна, меню, листы и карточки по MD3

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D6, D7, D8, D9, D10, D11, D12).

## Why

Контейнеры — последнее место, где Fluent виден с первого взгляда. Окно — `bg-surface` с рамкой и тенью
`shadow28`, заголовок 16/600, подвал отделён чертой всегда. Меню — пять разных видов: `bg-surface`,
рамка, радиус 6 или 10, тень 16 или 28. Тень 28 вне окна стоит в 17 файлах при правиле «только у
окна». Компонентов MD3 нет вовсе: card, divider, side sheet, bottom sheet. Карточки экраны рисуют
руками (`bg-surface border rounded-xl`, 40 строк в 35 файлах), боковые панели — каждая свою.

Экрану счёта по варианту Г нужен side sheet: перечень состояния счёта вместо поповера. Брать его
внутри change экрана нельзя — одна сессия, один change.

## What Changes

- **Окна.** `Modal` и `ConfirmDialog` — basic dialog MD3:
  - поверхность `surface-container-high`, радиус 28, возвышение 3, без рамки;
  - заголовок `headline-small`;
  - черта под шапкой и над подвалом — только когда тело прокручено;
  - действия справа.

  Крестик в шапке и одна filled среди действий — по решению В8: рекомендация зонтика «крестик
  оставить, filled одна» становится отступлением D8. Окно во весь экран (`fullScreen`) — full-screen
  dialog: шапка как top app bar с крестиком. Размеры окон (384/512/896/весь экран), «Назад» закрывает
  слой, шаги `WindowStep` и вопрос о несохранённом — без изменений смысла.
- **Меню.** Один общий компонент меню в `shared/ui` вместо пяти видов:
  - `surface-container`, радиус 4, возвышение 2;
  - пункт 48 (плотность −2 — 40, D7), ведущий знак 24, разделитель `outline-variant`;
  - на нём стоят `RowActionsMenu`, `SectionMenu`, `AccountMenu`, `WorkspaceSwitcher`, меню масштаба
    просмотрщика и пять меню экранов на Radix напрямую (`BaseInstanceChip`, `EstimateCard`,
    `InheritedFieldsPanel`, `RolePicker`, `ExportMenu`);
  - список `Select` и поповеры — на той же поверхности.
- **Карточки.** `Card` — outlined (12, `outline-variant`), filled (`surface-container-highest`),
  elevated (`surface-container-low`, возвышение 1). Правило храповика `hand-card` заводится с уровнем
  и только убывает; в ноль его доводит `sharedui-md3-17-screens`.
- **Разделитель.** `Divider` — 1 px `outline-variant`, во всю ширину или с отступом.
- **Side sheet.** `SideSheet`:
  - standard — сдвигает содержимое; первый потребитель — `RowPanel` таблиц;
  - modal — над содержимым, со scrim и возвышением 1, ведущие углы 16;
  - ширина ≤ 400, заголовок `title-large`, закрытие крестиком, Esc и «Назад».
- **Bottom sheet.** `BottomSheet` — верх 28, ручка, `surface-container-low`, scrim. Он же — вид
  modal side sheet на телефоне.
- Сторожа и документы: правило `hand-card`; `docs/UI.md` — «Окна», «Слои над окном», «Тени» и новые
  абзацы о листах и карточках; правки e2e, проверяющих размеры окон и меню, — вместе с компонентом.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: окна по basic dialog [UI-75], общее меню [UI-76], side sheet, bottom sheet, карточка и
  разделитель, правило `hand-card` [UI-77].

## Impact

- **Клиент:**
  - `shared/ui/Modal.tsx` (419 строк — в уровне размера, только худеет), `ConfirmDialog.tsx`,
    `RowActionsMenu.tsx`, `SectionMenu.tsx`, `AccountMenu.tsx`, `WorkspaceSwitcher.tsx`,
    `Select.tsx` (слой списка), `viewer/ViewerToolbar.tsx` (меню масштаба);
  - новые `Menu`, `Card`, `Divider`, `SideSheet`, `BottomSheet`;
  - `features/tables/RowPanel.tsx` — первый потребитель side sheet;
  - меню экранов на Radix напрямую: пять файлов в `features/`.
- **Голые `Dialog.Root`** (`PurgeDialog`, `PurgeBatchDialog`, `InvoicePlaceField`, `TypePicker`) и
  палитра команд меняют только поверхность и заголовок. Их логика закрытия своя, и переход на `Modal` в
  этот change не входит. Палитра — `sharedui-md3-15-shell`.
- **Сторожа:** `scripts/ui-ratchet-lib.mjs` (`hand-card`), `ui-baseline.json`, `ui-ratchet.test.mjs`;
  перечень правил в `CLAUDE.md` и `AGENTS.md` — одним коммитом.
- **Живые прогоны:** `dialogs-smoke`, `shared-ui-smoke`, `keyboard-smoke` и всё, что меряет окно
  или меню.
- **Claude Design:** `/design-sync` после слияния.
- **Сервер:** не меняется.
- **PR:** два — окна; меню, листы, карточки и разделитель. Оба с меткой `version:minor`.
