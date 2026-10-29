# Proposal: 15 ролей текста MD3

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D1, D2, D3, D10, D11).

## Why

Сегодня текст — это размеры, а не роли.

- **Шкала.** Ступени Tailwind `text-xs/sm/base`, свои `caption` 11 и `note` 13, вес и интервал — по
  месту. Одна ступень `text-xs` (1143 места) служит и подписью, и вторичным текстом. По MD3 это две
  роли: label и body.
- **Ступень 13.** `text-note` в MD3 нет.
- **Капс.** `uppercase tracking-wide` — 50 мест — остался от overline Material 2.
- **Вес.** `font-semibold` (79 мест) рисуется начертанием 700: 600 не загружен.

Вариант Г пишет текст ролями MD3: значение для чтения — body-large 16, заголовок сводки — title-large
22/28/400, подзаголовок — 14/20/500 без капса. Владелец выбрал его для всего приложения; судьбу `note`,
капса и интервала на кириллице решает В10.

## What Changes

- **15 ролей MD3:** display, headline, title, body, label — каждая large, medium, small. Роль —
  целиком в одном классе `text-<роль>`: размер, высота строки, межбуквенный интервал и вес (D1).
- **Механический перевод размеров кодмодом** (D3) по всему `src/`, вес не трогается:
  - `text-xs` → `text-body-small`;
  - `text-sm` → `text-body-medium`;
  - `text-base` → `text-body-large`;
  - `text-caption` → `text-label-small`.

  Остальное перечисляется отчётом.
- **Межбуквенный интервал** — по ролям MD3 везде, в таблицах тоже (В10, вариант «в»).
- **По месту, вторым PR:**
  - `text-note` → `body-medium` или `body-small`;
  - микрозаголовки `uppercase tracking-wide` → `title-small` без капса;
  - `text-lg/xl/2xl/3xl` → роль.

  Ступень `note` удаляется из темы (В10).
- **Правило храповика `legacy-type`:** ступени Tailwind, `caption`, `note`, `font-semibold/bold`,
  `uppercase`, `tracking-*`. Подсказка правила `text-size` называет роли. Перечень правил в `CLAUDE.md` и
  `AGENTS.md` — одним коммитом.
- **`docs/UI.md`:** разделы «Текст» и «Заголовки» переписываются ролями.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: роли текста MD3 — договор интерфейса и храповика [UI-67].

## Impact

- **Клиент:**
  - `src/client/src/index.css` / `src/theme/` — роли `--md-sys-typescale-*` и `--text-*`;
  - `scripts/md3-migrate.mjs` и карта — шаг текста;
  - `scripts/ui-ratchet-lib.mjs` и `ui-baseline.json`;
  - около 2200 мест в ~300 файлах.
- **Документы:** `docs/UI.md`, `CLAUDE.md` и `AGENTS.md` (одним коммитом), `/design-sync` (D12).
- **Сервер:** не меняется.
- **PR:**
  - PR 1 — роли, механический перевод и правило;
  - PR 2 — `note`, капс и крупные ступени по месту.

  Оба с меткой `version:minor`.
