# Proposal: словарь ролей MD3 и переходные псевдонимы — без видимых изменений

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D1, D2, D4, D11, D12).

## Why

Владелец 27.10.2026 перевёл интерфейс на чистый MD3 (зонтик, `proposal.md`). Первый шаг — словарь: пока
цвет называется `--f-*` и `text-fg1`, `bg-base`, `brand*`, любой следующий шаг пишет на языке Fluent, а
экран счёта по варианту Г писать не на чем. Шаг обязан не менять ни пикселя: тогда его можно сливать в
любой день, а не в «тихую неделю», которой не будет (D2).

## What Changes

- **Описание темы** `src/client/src/theme/md3-theme.json` в режиме `values`: полный набор ролей MD3 для
  обеих тем с сегодняшними значениями, свои роли `success`/`warning` и половины превью значка, уровни
  тени, список старых имён. Генератор `src/client/scripts/md3-theme.mjs` (`npm run md3:theme`) пишет из
  него CSS-переменные `--md-sys-*` / `--md-ext-*`, слой Tailwind с ролями MD3 и слой псевдонимов.
- **Классы ролей MD3** появляются рядом со старыми: `bg-primary`, `text-on-surface-variant`,
  `bg-surface-container-high`, `border-outline-variant`, `bg-inverse-surface`, `bg-scrim` (D1).
- **Старые имена** (`text-fg1`, `bg-base`, `bg-brand`…) — переходные псевдонимы ролей и рисуют ровно
  то же, что сегодня (D2). Расти им запрещает правило храповика `legacy-class`.
- **BREAKING (для кода, не для вида).** Токены `--f-*` удаляются целиком: из `index.css`, из 57 мест в
  43 файлах, из скриптов сторожей, из комментария `BrandIconComposer.cs`. Возврат ловит правило
  `legacy-token` с уровнем 0. Мёртвый `src/App.css` удаляется.
- **Сторожа:** тест темы (дрейф: перегенерация равна файлам в git; псевдоним равен своей роли) и сверка
  значений цвета сервера с палитрой; правило `white-on-fill` узнаёт заливки ролей MD3.
- **Документы:** `docs/UI.md` — «Язык формы: Material 3» вместо «гибрида» и «Цвет» на ролях MD3;
  `CLAUDE.md` и `AGENTS.md` — одним коммитом. Дизайн-система Claude Design пересинхронизируется.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: словарь цвета — роли MD3 под их именами [UI-57], переходные псевдонимы и запрет их роста
  [UI-58], сервер согласован с палитрой [UI-59].

## Impact

- **Клиент:** `src/client/src/index.css`, новый каталог `src/client/src/theme/` (описание и три
  сгенерированных файла), `scripts/md3-theme.mjs`, `scripts/md3-theme.test.mjs`,
  `scripts/ui-ratchet-lib.mjs` (+ тест), `scripts/color-classes.test.mjs` (фикстуры, совет),
  `ui-baseline.json`, `package.json` (команда `md3:theme`); 43 файла с `var(--f-*)`. Новых зависимостей
  нет.
- **Сервер:** `BrandIconComposer.cs` — только комментарий; значения не меняются.
- **Документы:** `docs/UI.md`, `CLAUDE.md` + `AGENTS.md`.
- **Claude Design:** `/design-sync` дизайн-системы «BHS.CRG UI» после слияния — из основной копии.
- **PR:** один, без метки `version:minor`: вид и поведение не меняются, это механика.
