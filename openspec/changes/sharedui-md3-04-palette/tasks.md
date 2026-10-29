# Tasks: палитра из seed, свои роли и две роли текста

Два PR после слияния `sharedui-md3-02-class-codemod`, оба с меткой `version:minor`.

## 0. До кода

- [x] 0.1 Лист в Claude Design — три варианта схемы на одной доске (зонтик, D10): нынешние значения,
  `TonalSpot`, `Fidelity` (или `Content`) из seed `#1d5fb0`; обе темы; на реальных местах (реестр счетов,
  счёт, окно, оболочка); у каждого — `fg3`/`fg4` как `on-surface-variant`, success/warning с гармонизацией
  и без, обводка предупреждения с рамкой и без. Проверка — ссылка на лист в зонтике и в этом change
  Лист «Палитра MD3 · схема из seed» (29.10.2026): https://claude.ai/design/p/af61dae9-6aea-4c13-9f49-7f5824d31641 — 0 «было», А TonalSpot, Б Fidelity, В Content;
  переключатели В1–В3 и обводки, контраст пар под каждым кадром. MCU 0.4.0 в Node не грузится (импорт
  без расширения в `color_spec_2025.js`, `scheme_*.js`) — лист посчитан на 0.3.0; учесть в 1.1.
- [x] 0.2 Решения владельца В1, В2, В3 и выбранный вариант схемы записаны в зонтике (`tasks.md`, 0.1, с
  датой) и перенесены сюда `/opsx:update`: `variant`, `pinPrimary`, `harmonize`, судьба
  `warning-border`/`stroke-subtle` — в `design.md` и требованиях [UI-62] [UI-63]. Проверка —
  `openspec validate sharedui-md3-04-palette --strict`; в `design.md` нет «по листу» без значения

## 1. Палитра (PR 1)

- [x] 1.1 `@material/material-color-utilities` — devDependency с точной версией; генератор в режиме
  `seed`: схема по `variant` и `contrast`, `pinPrimary`, свои роли по четыре в обеих темах, `static`
  tab-light/dark [UI-62]. Проверка — тест дрейфа зелёный; смена seed во временной копии описания меняет
  роли обеих тем; `npm run build` — в бандле нет кода MCU (поиск по имени модуля в `dist`)
- [x] 1.2 Тест контраста в `md3-theme.test.mjs` по парам `design.md`, обе темы, с фикстурой плохой палитры
  [UI-62]. Проверка — зелёный на выбранной схеме; фикстура плохой палитры — красный с парой, темой и числом
- [x] 1.3 Псевдонимы `fg3`, `fg4` → `on-surface-variant`; `--legacy-fg3` удалён [UI-63]. Проверка — тест
  «псевдоним ≡ роль» зелёный и для них
- [x] 1.4 `BrandIcons.BackgroundColor` и `BrandIconComposer.DarkTile` — значения генератора [UI-62].
  Проверка — сверка сервера в `md3-theme.test.mjs` зелёная; `dotnet test … --filter
  "FullyQualifiedName~Branding"` зелёный
- [x] 1.5 Снимки 5–7 ключевых экранов до и после в обеих темах на 1366×768 и 375 px (зонтик, 5.2).
  Проверка — снимки в описании PR
  Снимки 7 экранов (29.10.2026, база посева живых прогонов): https://claude.ai/artifact/7nkk4dyet9uPDBWk4nC4pP

## 2. Две роли текста (PR 2)

- [x] 2.1 Строки карты `md3-class-map.json` (`same: false`): `text-fg3`, `text-fg4`, `text-stroke-strong`
  → `text-on-surface-variant`; правило «текст на контейнере в одном списке классов» → `on-*-container`
  [UI-62] [UI-63]. Проверка — vitest кодмода на фикстурах; тест пар не требует равенства у `same: false`
- [x] 2.2 `npm run md3:migrate` по `src/`; места из отчёта (разнесённые по элементам контейнеры, прочие
  утилиты с `fg3`/`fg4`) — руками; `border-warning-border`, `border-stroke-subtle` — по решению 0.2;
  `--legacy-warning-border`, `--legacy-stroke-subtle` удалены [UI-62] [UI-63]. Проверка —
  `rg "fg3|fg4|stroke-strong|warning-border|stroke-subtle" src/client/src` — только `md3-legacy.css`
  (псевдонимы до финала эпика) и отчётные исключения в описании PR
- [x] 2.3 Уровень `legacy-class` опущен; по снятым здесь именам — 0 [UI-63]. Проверка — `npm run ui:ratchet`;
  тест правила: `text-fg4` в новом файле красный с советом `text-on-surface-variant`
- [x] 2.4 `docs/UI.md` «Цвет»: палитра из seed (`md3-theme.json`, `npm run md3:theme`), тест контраста,
  две роли текста, текст на контейнере — `on-*-container`, обводки по статусу нет (или как решено)
  [UI-62] [UI-63]. Проверка — в «Цвет» нет `fg3`, `fg4`, `warning-border` вне колонки псевдонимов
- [ ] 2.5 После слияния — `/design-sync` из основной копии. Проверка — токены бандла дизайн-системы равны
  `md3-tokens.css`

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Branding"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (дрейф, контраст, сервер)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 `cd src/client && npm run -s test:e2e:ci` — весь список живых прогонов зелёный в каждом PR:
  палитра общая для всех экранов (зонтик, 5.1)
