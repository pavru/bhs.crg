# Tasks: таблица данных, сетка правки и списки по MD3

## 0. До кода

- [ ] 0.1 Предусловия сверены: слиты и заархивированы change задачи 0.2 зонтика и дети
  `sharedui-md3-05-state-motion`, `-06-shape-elevation`, `-07-typescale`; желательно и
  `sharedui-md3-10-actions` (кнопки-иконки 40 в строках). Проверка — их нет в `openspec/changes`
- [ ] 0.2 Решение В5 (таблица плотности D7; что остаётся ниже −2 — `EditGrid sheet`, `Select sm` в
  клетках) принято и записано в зонтик с датой. Проверка — у В5 стоят дата и выбор
- [ ] 0.3 Разбор с Дизайнером и Архитектором (переменные плотности в `dataTable.ts`, класс клетки
  правки) — комментарием к задаче (`docs/UI.md` «Новый экран: порядок»). Проверка — ссылка в описании PR 1
- [ ] 0.4 Три макета в Claude Design на реальных таблицах на 1366×768 — реестр счетов, строки счёта,
  матрица разноски, таблица в окне (`compact`), список list-detail — в обеих темах. Три разных решения;
  планируются до 14.12.2026 или по В12. Проверка — ссылка на проект в задаче
- [ ] 0.5 Выбор владельца записан; перенесён `/opsx:update sharedui-md3-14-data` — высоты, роли шапки и
  сценарии [UI-78] по выбранному макету и В5. Проверка — `openspec validate sharedui-md3-14-data --strict`

## 1. PR 1: таблица данных и списки

- [ ] 1.1 `dataTable.ts`: переменные `--dt-head` / `--dt-row` для −1 (56/48) и −2 (48/40), шапка без
  заливки `title-small` `on-surface`, черты `outline-variant`, `dtCard` — outlined card 12, наведение —
  слой состояния [UI-78]. Проверка — vitest `DataGrid.test.tsx`: значения переменных по `density`
- [ ] 1.2 `DataGrid`: открытая строка `secondary-container` через `--dt-row-bg` (и закреплённые клетки),
  `marked` — `warning-container`, итог — `surface-container` [UI-78]. Проверка — vitest: фон закреплённой
  клетки открытой строки равен фону строки; ожидания классов переписаны вручную, с отрицательными (D11)
- [ ] 1.3 Списки: `ListDetailShell`, `PageList`, `panelLink` — строка 48 / 52…64, выбранная
  `secondary-container`, вторая строка `body-medium` `on-surface-variant` [UI-78]. Проверка — vitest
  `listDetailView.test.tsx`; живой прогон `settings-smoke`: строка списка типов высотой 48
- [ ] 1.4 Живые прогоны, проверяющие высоты строк и шапок таблиц (`invoice-layout-checks`,
  `table-view-smoke`, `table-chips-smoke`, `invoices-smoke`), поправлены тем же PR; весь
  `npm run -s test:e2e:ci` зелёный. Проверка — описание PR
- [ ] 1.5 `docs/UI.md`: «Таблицы» и «Плотность» — таблица D7 словами MD3; data table — в разделе
  отступлений (D8), без слова «MD3» рядом [UI-78]. Проверка — ревью PR; `cd docs/tools && npm run toc`,
  если менялись главы

## 2. PR 2: сетка правки

- [ ] 2.1 `EditGrid`: строка 48, класс клетки правки `dtCellField` (без рамки и заливки, подчёркивание
  `primary` у фокуса, `error` у ошибки), итог — `surface-container` [UI-78]. Проверка — vitest
  `EditGrid.test.tsx`: класс клетки и подчёркивание по `aria-invalid`
- [ ] 2.2 `sheet` — по В5 (рекомендация: строка 40, линии колонок `outline-variant`) [UI-78]. Проверка —
  vitest: высота строки `sheet` по переменной; живой прогон `fields-smoke` для массива документа
- [ ] 2.3 Экраны с `EditGrid` (5 файлов, среди них строки счёта и матрица разноски) берут `dtCellField`
  вместо своих рамок клетки [UI-78]. Проверка — `rg "dtTdEdit|dtTdSheet"` только в `shared/ui`; тесты
  экранов зелёные
- [ ] 2.4 Живые прогоны с размерами сетки правки (`allocation-matrix-smoke`, `invoice-lines-smoke`,
  `fields-smoke`) поправлены тем же PR; весь `npm run -s test:e2e:ci` зелёный. Проверка — описание PR
- [ ] 2.5 Уровень `fixed-width` перепроверен: ширины колонок действий под кнопки 40, уровень не вырос.
  Проверка — `npm run ui:ratchet`
- [ ] 2.6 `/design-sync` после слияния PR 2: `DataGrid` и `EditGrid` в дизайн-системе — новый вид (D12).
  Проверка — превью компонентов

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный
- [ ] 3.4 В описании каждого PR — макет, выбранный вариант и ссылка; снимки 5–7 ключевых экранов до и
  после в обеих темах на 1366×768 и 375 px (зонтик, 5.2); весь `npm run -s test:e2e:ci` зелёный (5.1)
- [ ] 3.5 PR с меткой `version:minor`
