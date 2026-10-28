# Tasks: словарь ролей MD3 и переходные псевдонимы

Один PR, без метки `version:minor`. Макетов нет (зонтик, D10).

## 1. Описание темы и генератор

- [x] 1.1 `src/client/src/theme/md3-theme.json` в режиме `values`: все роли MD3 обеих тем (нынешние
  значения `--f-*`, недостающие — из схемы MCU для seed `#1d5fb0`), `ext` success/warning по четыре роли,
  `static` tab-light/dark, `elevation` 1–3 по темам, `aliases` по таблице `design.md` [UI-57] [UI-58].
  Проверка — каждое значение `--f-*` из `index.css` master встречается в описании ровно под той ролью или
  псевдонимом, что в таблице (сверка в тесте 1.3)
- [x] 1.2 `src/client/scripts/md3-theme.mjs` и `npm run md3:theme`: пишет `theme/md3-tokens.css`,
  `theme/md3-tailwind.css`, `theme/md3-legacy.css` с шапкой «сгенерировано»; повторный запуск файлов не
  меняет [UI-57]. Проверка — два запуска подряд, `git diff` после второго пуст
- [x] 1.3 `src/client/scripts/md3-theme.test.mjs`: дрейф трёх файлов; псевдоним ≡ роль через
  `candidatesToCss` с разрешёнными переменными в обеих темах [UI-57] [UI-58]. Проверка — тест зелёный; с
  правкой значения в `md3-tokens.css` руками и с псевдонимом `fg1 → on-surface-variant` — красный с
  понятным сообщением

## 2. `index.css` и удаление `--f-*`

- [x] 2.1 `index.css`: блоки `--f-*` и словарь Fluent в `@theme inline` удалены, три файла темы
  импортированы; база (`body`, поля в `@layer base`, `[data-modal-hint]`) на `--md-sys-*`; радиусы и
  `--font-mono` — числами в `@theme`; заголовок «Fluent 2 design tokens» снят [UI-57]. Проверка —
  `rg -e '--f-' src/client/src` пусто; `color-classes.test.mjs` зелёный
- [x] 2.2 57 мест `var(--f-*)` в 43 файлах заменены по таблице `design.md` (тени → `--md-sys-elevation-levelN`,
  `--f-brand` → `--md-sys-color-primary`…); комментарий `BrandIconComposer.cs` называет роль MD3; `src/App.css`
  удалён [UI-57]. Проверка — `rg -e '--f-' src` пусто (кроме истории в `docs/DEV_NOTES.md`, если есть)
- [x] 2.3 Доказательство «вид не изменился»: разовый прогон — объявления каждого старого класса × утилиты
  `COLOR_UTILITIES` по `index.css` master и ветки, с разрешёнными переменными, светлая и тёмная тема
  [UI-58]. Проверка — различий ноль, вывод и команда — в описании PR; плюс `SHOT=` трёх экранов (реестр
  счетов, окно, оболочка) до и после — совпадают на глаз

## 3. Сторожа храповика

- [x] 3.1 Правило `legacy-token` в `ui-ratchet-lib.mjs`: `--f-` и `--legacy-` вне `src/theme/`, код и
  стили, весь `src/`, уровень 0 [UI-57]. Проверка — `ui-ratchet.test.mjs`: `shadow-[var(--f-shadow16)]` в
  `.tsx` и `var(--f-brand)` в `.css` красные, `src/theme/md3-legacy.css` не считается
- [x] 3.2 Правило `legacy-class`: старые имена из `aliases` описания темы с любой цветовой утилитой и
  вариантом, `var(--color-<старое>)`; уровень — `npm run ui:ratchet:update` [UI-58]. Проверка — тест
  правила: `hover:bg-brand-subtle` и `var(--color-brand)` считаются, `text-success`, `bg-surface`,
  `text-tertiary`, `text-base` (размер) — нет; `ui-baseline.json` содержит уровень по файлам
- [x] 3.3 `white-on-fill`: `ROLE`/`FILL` на роли MD3 и переменные `--md-sys-*`/`--md-ext-*`, старые имена
  остаются до 20; фикстуры `ui-ratchet.test.mjs` без `--f-` [UI-58]. Проверка — тест: `bg-primary
  text-white` и `bg-[var(--md-sys-color-error)] text-white` красные, `bg-brand text-white` — тоже;
  `bg-primary-container text-white` — нет
- [x] 3.4 `color-classes.test.mjs`: фикстура произвольного значения на `--md-sys-*`, совет в сообщении —
  роли MD3 (`text-on-surface`, `bg-primary`) [UI-57]. Проверка — тест зелёный; класс `bg-surface2`
  по-прежнему красный и сообщение советует роль MD3

## 4. Сервер согласован с палитрой

- [x] 4.1 В `md3-theme.test.mjs` — сверка `BrandIcons.BackgroundColor` с `light.surface-container-low` и
  `BrandIconComposer.DarkTile` с `dark.surface`, чтение `.cs` текстом [UI-59]. Проверка — тест зелёный;
  с изменённым значением роли в описании и перегенерацией — красный и называет файл, константу и значение

## 5. Документы и дизайн-система

- [x] 5.1 `docs/UI.md`: «Язык формы: Material 3» вместо «гибрида» (отмена решения о гибриде — со ссылкой на
  зонтик); «Цвет» — таблица ролей MD3 с колонкой «старое имя, псевдоним до финала эпика»; «Нужного цвета
  нет» — роль в `md3-theme.json` и `npm run md3:theme`; «Тени» — `--md-sys-elevation-level1..3`; «Радиусы» без
  `--f-r-*`; «Кто сторожит» — `legacy-token`, `legacy-class`, сторож темы [UI-57] [UI-58] [UI-59].
  Проверка — `rg -e '--f-' docs/UI.md` пусто, слова «гибрид» в «Язык формы» нет
- [x] 5.2 `CLAUDE.md` и `AGENTS.md` одним коммитом: ссылка на `docs/UI.md` без «токенов `--f-*`», перечень
  правил храповика интерфейса с `legacy-token` и `legacy-class`. Проверка — `dotnet test … --filter
  AgentRulesCopy` зелёный
- [ ] 5.3 После слияния — `/design-sync` дизайн-системы «BHS.CRG UI» из основной копии (состояние
  `.design-sync/` только там): README «Styling idiom», `conventions.md` и правила соответствия — на ролях
  MD3. Проверка — в README дизайн-системы нет `text-fg1`, `bg-base`, «Fluent 2 scale». Change заархивирован с ней открытой (решение владельца 28.10.2026): делает основная сессия, отметка —
  в зонтике, 1.1

## 6. Верификация

- [x] 6.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Branding|FullyQualifiedName~AgentRulesCopy"` — зелёный
- [x] 6.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (тест темы, сторож словаря,
  храповик)
- [x] 6.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; `legacy-token` — 0
- [x] 6.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [x] 6.5 `cd src/client && npm run -s test:e2e:ci` — весь список живых прогонов зелёный: `index.css` общий для всех
  экранов (зонтик, 5.1). Сделано работой CI «Живые прогоны (браузер)» на #1674 (dccde3ca) — зелёная
