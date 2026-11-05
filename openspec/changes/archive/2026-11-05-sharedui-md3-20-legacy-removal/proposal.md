# Proposal: финал перехода на MD3 — псевдонимы удалены, словарь темы сторожит сам

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D1, D2, D8, D11, D12).

## Why

С `sharedui-md3-01-tokens` старые имена классов (`text-fg1`, `bg-base`, `border-stroke`, `bg-brand`…) живут
переходными псевдонимами ролей MD3 (D2). Их рост держит правило `legacy-class`, но только по списку
запретных слов. Псевдоним, переживший финал, — это вернувшийся «гибрид». Правило-список ловит лишь то,
что в него вписали.

Механизм, который ловит всё сразу, уже есть: класс без имени в `@theme` Tailwind не порождает, а
`color-classes.test.mjs` краснеет. Сбросив пространства имён темы, мы заставим его ловить любое забытое
старое имя, сырую палитру (`bg-gray-500`), `rounded-2xl`, `text-[11px]` и `shadow-lg` — одним сторожем.
Срок — не позже двух недель после `sharedui-md3-17-screens` (зонтик, 4.2).

## What Changes

- **Псевдонимы удалены**: в `@theme inline` остаются только роли MD3 (D1). Вид не меняется: после
  17 уровни `legacy-class` и пяти правил шкал — ноль, ни один файл старых имён не использует.
- **Сброс пространств имён в `@theme`**: `--color-*: initial`, `--radius-*: initial`,
  `--text-*: initial`, `--shadow-*: initial` (D2). Классы порождаются только из словаря MD3: ролей
  цвета, шкалы формы, возвышения, ролей текста.
- **Сторож словаря темы**: `color-classes.test.mjs` становится `theme-classes.test.mjs`. Он проверяет,
  что каждый класс цвета, `rounded-*`, `text-*` (размер) и `shadow-*` в `src/` есть в словаре темы.
  Отказ называет файл, класс и ближайшую роль MD3.
- **Правила снимаются**: `legacy-class`, `palette`, `text-size` — их работу делает словарь (D11).
  Правило `legacy-token` (`--f-`) остаётся навсегда.
- **Документы:**
  - `docs/UI.md` в финальном виде: «Язык формы: Material 3», раздел «Где мы отступаем от MD3 и почему»
    со всеми отступлениями D8 и решениями владельца (крестик окна, `danger`, banner, data table,
    `StatusText`, «Очистить / Сегодня», сегменты времени, всё ниже −2), раздел «Кто сторожит»;
  - `CLAUDE.md` и `AGENTS.md` — перечень правил храповика, одним коммитом.
- **Дизайн-система «BHS.CRG UI»**: README («Styling idiom») и `conventions.md` — только словарь MD3,
  правила соответствия (`_adherence.oxlintrc.json`) — на новый словарь, `/design-sync` — превью без
  lucide и без старых классов.
- **Снимки `docs/images`** переснимаются один раз, здесь. PDF инструкций пересобирается.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: язык интерфейса — только MD3; класс вне словаря темы не проходит сторожа [UI-82].

## Impact

- **Клиент:** `src/client/src/index.css` и `src/client/src/theme/*` (слой `@theme inline` из
  генератора `md3-theme.mjs`), `scripts/color-classes-lib.mjs` → `theme-classes-lib.mjs`,
  `scripts/color-classes.test.mjs` → `theme-classes.test.mjs`, `scripts/ui-ratchet-lib.mjs`,
  `ui-ratchet.test.mjs`, `ui-baseline.json`, `scripts/md3-class-map.json` (кодмод остаётся для чужих
  веток — до их слияния).
- **Документы:** `docs/UI.md`, `CLAUDE.md`, `AGENTS.md`, `docs/images/*` (31 снимок: `USER_GUIDE.md` —
  20 ссылок, `ADMIN_GUIDE.md` — 12), PDF инструкций (`docs/tools`).
- **Claude Design:** README, `conventions.md`, `_adherence.oxlintrc.json`, превью `.design-sync` — только
  в основной копии, в worktree копируются туда и обратно (D12).
- **Сервер:** не меняется; сторож «сервер ↔ палитра» из 01 остаётся.
- **Вид:** не меняется. Доказательство — пустой уровень правил до PR и зелёный сторож словаря после.
- **PR:** один, метка `version:minor`.
