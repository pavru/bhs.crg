# Proposal: обратная связь по MD3 — snackbar, подсказки, banner, пометка состояния, badge, progress

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D8, D9, D10, D11).

## Why

Обратная связь нарисована словарём Fluent. В MD3 у каждого её вида есть свой компонент или названное
расширение.

| Что | Сейчас | По MD3 |
|---|---|---|
| Уведомление | стопка до трёх справа снизу; знак варианта; ошибка — на красной подложке; 380 px, радиус 10, тень | snackbar: по одному, `inverse-surface`, без цветных вариантов |
| Подсказка | поверхность с рамкой и тенью | простая подсказка (plain tooltip) на `inverse-surface`, без рамки и тени |
| Подсказка с заголовком и действием | нет вовсе | богатая подсказка (rich tooltip) |
| Пометка состояния | пилюля с заливкой тоном (`StatusBadge`) | пометки нет; в варианте Г — текст роли с ведущим знаком |
| Счётчик | пилюли руками | badge |
| Ход операции | полосы руками | linear progress |
| Плашки-сообщения | `*-subtle + border + rounded`: 46 строк в 31 файле, 14 компонентов по имени | banner — расширение из Material 2, D8 |

Ни одна плашка-сообщение не похожа на другую.

Владелец выбрал вариант Г для всего приложения. Snackbar по одному — изменение поведения, его решает В9.

## What Changes

- **`Snackbar` вместо `Toast`** (рекомендация В9):
  - `inverse-surface` / `inverse-on-surface`, `body-medium`, форма 4, уровень 3;
  - действие — `inverse-primary`;
  - **по одному**: следующий — после закрытия текущего; ошибка не вытесняется;
  - знаков и красной подложки у ошибки нет;
  - «Сообщить» у ошибки с идентификатором запроса остаётся.

  Хук `useToast` → `useSnackbar` с теми же методами — кодмодом по 93 местам.
- **`Tooltip`** — простая подсказка MD3: `inverse-surface`, `body-small`, форма 4, без рамки и тени.
  Договор прежний: имя кнопки-иконки, `aria-description`, неактивная кнопка остаётся в Tab, Esc
  закрывает подсказку.
- **`RichTooltip`** (новый) — богатая подсказка:
  - `surface-container`, форма 12, уровень 2;
  - заголовок `title-small`, текст `body-medium`, до двух text-действий;
  - с действием не исчезает, пока указатель или фокус внутри.
- **`Banner`** (новый, расширение D8) — сообщение во всю ширину вверху своей области:
  - знак 24 в круге 40 цвета контейнера роли;
  - текст `body-medium`, до двух text-действий;
  - разделитель снизу.

  Плашки `shared/` переводятся (`RereadFailedNotice`). Правило `notice-plaque` ставит уровень
  самоделкам `features/` до 17.
- **`StatusText` вместо `StatusBadge`** (как в варианте Г):
  - `label-medium` цвета роли;
  - ведущий знак 16 с заливкой — у тонов `success`, `warning`, `error` обязателен;
  - точка «·» между соседними.
- **`Badge`** (новый) — точка 6 px или счётчик 16 px на знаке, пункте навигации и вкладке.
- **`LinearProgress`** (новый):
  - с долей — для хода с известной долей;
  - без доли — для неизвестной.

  Самодельные полосы переводятся: `InvoiceScanDrop`, `ParseReport`.
- **`docs/UI.md`.** Строки «Уведомление», «Подсказка», «Пометка состояния» таблицы контролов; раздел
  отступлений — `Banner` и `StatusText` (D8).

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: snackbar [UI-71], подсказки [UI-72], banner [UI-73], пометка состояния, badge и progress
  [UI-74]. После архива `sharedui-chip-badge-tabs-screens` его [UI-43] и [UI-45] правятся здесь же
  (`tasks.md`, 0.5).

## Impact

- **Клиент:**
  - `shared/ui/Toast.ts` и `ToastProvider.tsx` → `Snackbar`;
  - `Tooltip.tsx`, новые `RichTooltip`, `Banner`, `Badge`, `LinearProgress`;
  - `StatusBadge.tsx` → `StatusText`, `RereadFailedNotice.tsx`;
  - `useToast` — 93 места в 83 файлах;
  - `StatusBadge` — 25 мест в 14 файлах;
  - `Tooltip` — 276 мест, вызовы не меняются;
  - `ActivityCenter` и счётчики вкладок;
  - `scripts/ui-ratchet-lib.mjs`;
  - e2e, читающие тост, — по роли и тексту, классов в них нет.
- **Документы:** `docs/UI.md`, `CLAUDE.md` и `AGENTS.md` (`notice-plaque`, одним коммитом),
  `/design-sync` (D12).
- **Сервер:** не меняется.
- **PR:**
  - PR 1 — `Snackbar`, `Tooltip`, `RichTooltip`;
  - PR 2 — `Banner`, `StatusText`, `Badge`, `LinearProgress`, `notice-plaque`.

  Оба с меткой `version:minor`.
