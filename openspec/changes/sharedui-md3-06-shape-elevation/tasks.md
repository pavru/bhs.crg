# Tasks: шкала формы и уровни возвышения

## 0. До кода

- [ ] 0.1 Лист с вариантами на доске Claude Design (зонтик, D10): одна плоскость, три варианта рядом,
  обе темы. Варианты:
  - (а) MD3 4/8/12/16/28 и уровни MD3;
  - (б) MD3-форма с нынешними тенями;
  - (в) нынешние 4/6/10/16/28.

  На каждом варианте — поле, меню над окном, карточка, чип, окно. Проверка — ссылка на лист в задаче
  зонтика 0.1.
- [ ] 0.2 Решение владельца В11 записано в зонтике (дата и выбор). Если выбор расходится с дельтой —
  `/opsx:update sharedui-md3-06-shape-elevation`: design и спецификация [UI-66] переписаны. Проверка —
  `openspec validate sharedui-md3-06-shape-elevation --strict`.
- [ ] 0.3 Сверка с 01 и 02, когда они слиты. Под каким именем тени и радиусы дожили до 06, есть ли в
  карте `md3-class-map.json` шаг радиусов. Расхождение — в `design.md`, «Context». Проверка — абзац
  «После 01» называет фактические имена.

## 1. Шкала в теме

- [ ] 1.1 `--md-sys-shape-corner-*` и `--md-sys-elevation-level0..5` (значения Material Web, обе темы
  одинаково). Уровни — в разделе `elevation` описания темы `md3-theme.json` и через генератор 01, а не
  руками в CSS. В `@theme` — `--radius-xs/sm/md/lg/xl` и
  `--shadow-elevation-1..5`. Прежние `--radius-*` удалены [UI-66]. Проверка: vitest
  `themeTokens.test.ts` (или `md3-theme.test.mjs`) сверяет пять ступеней и пять уровней. При
  намеренно сломанном значении тест красный.

## 2. Карта радиусов и теней

- [ ] 2.1 Шаг радиусов в `scripts/md3-migrate.mjs` и `md3-class-map.json`: одна цепь (см. design), с
  префиксами сторон и углов и с вариантами. Граница «до перехода» — по `git blame` и метке шкалы.
  `rounded-2xl/3xl` и `rounded-[…]` — в отчёт [UI-66]. Проверка: тест кодмода на фикстурах.
  - `rounded-md` → `rounded-sm` один раз;
  - повторный запуск ничего не меняет;
  - строка коммита с меткой не сдвигается;
  - `rounded-md` с `md:` сохраняет вариант.
- [ ] 2.2 Шаг теней: `shadow-[var(--md-sys-elevation-level1..3)]` → `shadow-elevation-1..3`, `boxShadow` с
  этими переменными в стиле → класс. Отчёт
  называет тени окна вне `Modal`/`ConfirmDialog` [UI-66]. Проверка — тест кодмода; отчёт приложен к PR.
- [ ] 2.3 Прогон по всему `src/`. Руками по отчёту:
  - меню, поповеры, палитра, `ViewerToolbar` → `elevation-2`;
  - `PrimaryFab` → `elevation-3`;
  - `Modal` → `rounded-xl`.

  [UI-66]. Проверка:
  - `rg "rounded-\[28px\]|shadow-\[var\(--md-sys-elevation" src/client/src` — пусто;
  - в `shared/` нет `rounded-2xl/3xl` и `boxShadow`; остаток в `features/` — в уровне `legacy-shadow`.

## 3. Сторожа

- [ ] 3.1 Правила `legacy-radius` и `legacy-shadow` в `ui-ratchet-lib.mjs` (`everywhere: true`, по
  файлам), уровень — после 2.3 [UI-66]. Отказ называет ступени и уровни и раздел `docs/UI.md`.
  Проверка — `ui-ratchet.test.mjs`:
  - фикстура с `rounded-2xl`, `shadow-lg`, `style={{ boxShadow }}` красная;
  - с `rounded-sm` и `shadow-elevation-2` — зелёная.
- [ ] 3.2 `PILL` у `chip` и `badge` — `rounded-(?:full|sm)` [UI-66]. Замер новых попаданий после
  карты: число по правилам — в описании PR, разбор с Дизайнером. Самоделки — в уровень с перечнем;
  законные формы — сужение правила и фикстура. Проверка — `ui-ratchet.test.mjs`: фикстура
  `<button className="rounded-sm px-3 border">` краснит `chip`; при старом `PILL` тест красный (граница
  к прогону).
- [ ] 3.3 Перечень правил храповика в `CLAUDE.md` и `AGENTS.md` — одним коммитом. Проверка —
  `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter AgentRulesCopy` зелёный.

## 4. Документы и носители

- [ ] 4.1 `docs/UI.md`:
  - «Радиусы» и «Тени» заменены таблицей «Форма и возвышение» (design), с ребёнком у каждой строки;
  - в «Кто сторожит» — `legacy-radius`, `legacy-shadow`, новая форма у `chip` и `badge`;
  - рядом с командой D3 — порядок для чужой ветки на шаге радиусов: сначала коммит слияния.

  [UI-66]. Проверка — ревью PR; в `docs/UI.md` нет `--f-shadow`, `--f-r-`, 6 px и 10 px.
- [ ] 4.2 `/design-sync` дизайн-системы «BHS.CRG UI» (D12): README и `conventions.md` без «Fluent 2
  scale», бандл со шкалой. Проверка — README называет `rounded-xs…xl` = 4/8/12/16/28 и
  `shadow-elevation-*`.

## 5. Верификация

- [ ] 5.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный.
- [ ] 5.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста, кроме названного
  в 3.2.
- [ ] 5.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FileSizeRatchet|AgentRulesCopy"`
  — зелёный.
- [ ] 5.4 `npm run -s test:e2e:ci` — весь список, зелёный (зонтик, 5.1).
- [ ] 5.5 Снимки 5–7 ключевых экранов до и после, обе темы, 1366×768 и 375 px (зонтик, 5.2). Отдельно —
  меню над окном в тёмной теме. Проверка — описание PR.
- [ ] 5.6 PR с меткой `version:minor`. В описании:
  - отчёт карты;
  - замер `PILL`;
  - команда обновления чужой ветки.
