# Tasks: слой состояния, фокус, отключённое и движение

Два PR после слияния `sharedui-md3-02-class-codemod` и `sharedui-md3-04-palette`, оба с меткой
`version:minor`.

## 0. До кода

- [ ] 0.1 Лист в Claude Design — доска состояний (зонтик, D10): слой 8/10/10 на всех заливках (filled,
  tonal, outlined, text, пункт меню, строка таблицы, рейл) в обеих темах, отключённое 38/12, три варианта
  кольца фокуса А/Б/В из `design.md` на реальных местах (форма, тулбар таблицы, клетка `EditGrid`).
  Проверка — ссылка на лист в зонтике и в этом change
- [ ] 0.2 Выбор владельца (кольцо фокуса; наведение в плотной таблице — по MD3 или с оговоркой) записан в
  зонтике (`tasks.md`, 0.1, с датой) и перенесён сюда `/opsx:update` в `design.md` и требование [UI-64] о
  фокусе. Проверка — `openspec validate sharedui-md3-05-state-motion --strict`; в `design.md` один вариант
  кольца

## 1. Основа и `shared/ui` (PR 1)

- [ ] 1.1 Токены `--md-sys-state-*` и `--md-sys-motion-*` в описании темы и генераторе; `--ease-*` в
  `@theme` [UI-64] [UI-65]. Проверка — тест дрейфа зелёный; класс `ease-standard` существует для сторожа
  словаря
- [ ] 1.2 `@utility state-layer` (D6 + `data-[highlighted]`, `[data-dragging]`, `--_state-color`) и
  `@utility focus-ring` по выбору 0.2 [UI-64]. Проверка — vitest/jsdom: класс даёт `background-image` с
  `color-mix`; наведение, фокус и `aria-disabled` меняют `--_s`
- [ ] 1.3 `shared/ui` на `state-layer`, `focus-ring` и паре отключённого: `Button`, `IconButton`, `Chip`,
  `Tabs`, `Select` и пункты меню, `DataGrid`, `EditGrid`, `SectionRail`, `BottomNav`, `RowActionsMenu`,
  `Calendar`, `Switch`, `Checkbox`; `brightness-*`, `brand-hover/pressed`, `opacity-*` у отключённого в
  `shared/ui` — ноль [UI-64]. Проверка — vitest компонентов, проверяющих классы, переписаны на новую
  модель; `rg "brightness-|brand-hover|brand-pressed|disabled:opacity" src/client/src/shared` пусто
- [ ] 1.4 Глобальный `transition` на `*` и `.no-transition` удалены; переходы компонентов —
  `ease-standard`; правило `prefers-reduced-motion` в базе, индикатор хода — исключение [UI-65]. Проверка —
  живой прогон или vitest: вычисленный `transition` произвольного `div` — `none`; у кнопки — с кривой
  `standard`
- [ ] 1.5 Правила `raw-state` и `dark-variant` (по синтаксическому дереву строк классов, весь `src/`) с
  уровнями по файлам [UI-64]. Проверка — `ui-ratchet.test.mjs`: `hover:bg-black/5`, `bg-white/10`,
  `hover:brightness-95`, `hover:bg-surface-container`, `dark:hover:bg-white/10` считаются; `bg-scrim/32` и
  регулярка со строкой `dark:` — нет
- [ ] 1.6 Снимки 5–7 ключевых экранов до и после в обеих темах на 1366×768 и 375 px (зонтик, 5.2);
  отдельно — наведение, фокус и отключённое на кнопках и в таблице. Проверка — снимки в описании PR

## 2. Экраны (PR 2)

- [ ] 2.1 Строки карты кодмода (`same: false`): самодельное наведение → `state-layer`,
  `disabled:opacity-*` → пара отключённого, `brand-hover`/`brand-pressed` → снять, `text-brand-hover` без
  наведения → `text-primary`, `bg-black/40` → `bg-scrim/32` [UI-64]. Проверка — vitest кодмода на
  фикстурах; второй прогон — «замен 0»
- [ ] 2.2 `npm run md3:migrate` по `features/`, остаток отчёта — руками; белый лист просмотрщика — в CSS
  просмотрщика; `fg-disabled` → `text-on-surface/38`; `--legacy-*` удалены целиком [UI-64]. Проверка —
  `rg "dark:" src/client/src --glob '*.tsx'` — только не-классы; `md3-legacy.css` без `--legacy-`
- [ ] 2.3 Уровни `raw-state`, `dark-variant`, `legacy-class` — 0 [UI-64]. Проверка — `npm run ui:ratchet`
  зелёный, в `ui-baseline.json` правила пусты
- [ ] 2.4 `docs/UI.md`: разделы «Состояния» (слой, фокус, отключённое) и «Движение» (токены, без
  глобального перехода, уменьшенное движение); «Цвет» — без `bg-black/5` и `dark:` как законных; «Кто
  сторожит» — `raw-state`, `dark-variant`; перечень правил в `CLAUDE.md` и `AGENTS.md` одним коммитом
  [UI-64] [UI-65]. Проверка — `dotnet test … --filter AgentRulesCopy` зелёный
- [ ] 2.5 После слияния — `/design-sync` из основной копии. Проверка — в бандле дизайн-системы нет
  `brightness-` и `bg-black/5`

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; `raw-state`,
  `dark-variant`, `legacy-class` — 0
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 `cd src/client && npm run -s test:e2e:ci` — весь список живых прогонов зелёный в каждом PR:
  меняются общие компоненты и оболочка (зонтик, 5.1)
