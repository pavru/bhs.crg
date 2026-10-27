# Tasks: действия по MD3

## 0. До кода

- [ ] 0.1 Предусловия слиты:
  - зонтик 0.2 — `sharedui-icon-button-screens`, `sharedui-chip-badge-tabs-screens`,
    `sharedui-checkbox-screens`, `sharedui-page-menus-radix` заархивированы;
  - зонтик 2.1, 2.3–2.5 — значки, состояние, форма, текст.

  Проверка — уровни `icon-button` и `chip` в `ui-baseline.json` равны нулю или названы исключением;
  в `openspec/changes` нет этих четырёх каталогов.
- [ ] 0.2 Разбор с Дизайнером и Архитектором (слой `shared/ui`) — комментарием к задаче (`docs/UI.md`,
  «Новый экран: порядок», шаг 1). Проверка — ссылка на разбор в задаче.
- [ ] 0.3 Три макета в Claude Design — разные решения, а не три цвета:
  - (А) MD3 40 везде, в строках таблиц тоже;
  - (Б) кнопки 40, кнопки-иконки в строках таблиц 32, как в варианте Г, — отступление;
  - (В) кнопка `dense` 32 в строках таблиц и панелях инструментов — альтернатива В7.

  На каждом, в обеих темах, 1366×768 и 375 px:
  - шапка реестра с действиями;
  - строка таблицы с действиями;
  - панель отбора с чипами filter, input, assist;
  - окно с filled и `danger`;
  - сегментный переключатель;
  - FAB на телефоне.

  Проверка — ссылки на три макета в задаче.
- [ ] 0.4 Выбор владельца (pavru) и решение В7 записаны в зонтике с датой. Выбор перенесён в `design.md`
  и [UI-68] через `/opsx:update sharedui-md3-10-actions`. Проверка —
  `openspec validate sharedui-md3-10-actions --strict`; в design назван макет и вариант.
- [ ] 0.5 После архива `sharedui-chip-badge-tabs-screens` и `sharedui-icon-button-screens` — дельта
  дополнена через `/opsx:update`:
  - MODIFIED [UI-42] — режим внутри панели становится `SegmentedButton`, тонов чипа нет;
  - MODIFIED [UI-46] — если в нём названы размеры кнопки-иконки.

  Проверка — `openspec validate … --strict`; в дельте нет `Tabs variant="segmented"` как правила.

## 1. PR 1 — кнопки, кнопки-иконки, FAB

- [ ] 1.1 `Button`: 40 px, `label-large`, четыре вида по MD3, filled без тени (уровень 1 при
  наведении), знак 18, отступы 24/16/12. `size` удалён из типа. `danger` и `multiline` — отступления
  [UI-68]. Проверка — vitest `Button`:
  - класс высоты один;
  - у filled нет `shadow-elevation-*` без наведения;
  - `danger` filled — на `error`.
- [ ] 1.2 Кодмод снимает `size=` у `Button` и `IconButton` по имени тега. Места, где `size` был
  выражением, — в отчёт и руками [UI-68]. Проверка:
  - `npx tsc -b` без ошибок;
  - `rg '<(Button|IconButton)[^>]*\bsize=' src/client/src` — пусто.
- [ ] 1.3 `IconButton`: 40×40, знак 24, `variant` (standard, filled, tonal, outlined), `selected` →
  `aria-pressed` и `filled` у знака; `danger` — отступление [UI-68]. Проверка — vitest:
  - `selected` ставит `aria-pressed` и заливку знака;
  - без `selected` атрибута нет;
  - `label` — имя кнопки.
- [ ] 1.4 Цель нажатия 48 px под `pointer: coarse` у `Button` и `IconButton` [UI-68]. Проверка — живой
  прогон `shared-ui` на 375 px: касание в 4 px за краем кнопки-иконки нажимает её.
- [ ] 1.5 `PrimaryFab` — extended FAB: `primary-container`, форма 16, уровень 3 [UI-68]. Проверка —
  живой прогон оболочки на телефоне находит FAB 56 px; `style` у него нет.
- [ ] 1.6 Живые прогоны, проверяющие высоты и ширины кнопок, правятся тем же PR: `rail-checks`,
  `invoice-layout-checks`, `fields-smoke`, `allocation-matrix-smoke`, `invoice-payment-smoke`,
  `table-view-smoke` — по факту прогона. Проверка — каждое ожидание названо в PR, весь список зелёный.
- [ ] 1.7 `docs/UI.md`:
  - строка «Кнопка» таблицы контролов — 40, четыре вида, одна filled, `IconButton` с видами и
    переключением;
  - в разделе отступлений — `danger` и многострочная кнопка.

  [UI-68]. Проверка — ревью; в `docs/UI.md` нет `size="sm"` у кнопок.

## 2. PR 2 — чипы и сегменты

- [ ] 2.1 `Chip`: 32 px, форма 8, `label-large`. filter (выбранный — `secondary-container` с галочкой 18,
  без контура), input (крестик — своя кнопка), assist (вместо `dashed`). `tone` и `dashed` удалены,
  `iconTone` у знака. `count`, `menu`, `role="radio"` — как были [UI-68]. Проверка — vitest `Chip`:
  - у выбранного нет контура и есть галочка;
  - `aria-pressed` у переключателя;
  - `aria-checked` у `role="radio"`.
- [ ] 2.2 Места `tone`, `dashed` (12 файлов) переведены на `iconTone` и assist chip [UI-68]. Проверка —
  `npx tsc -b` без ошибок; `rg "dashed|tone=" src/client/src/features` у `<Chip` пусто.
- [ ] 2.3 `shared/filter/FilterChips` — input chip; правка условия по нажатию — как была [UI-68].
  Проверка — живые прогоны `table-chips` и `table-saved-views` зелёные.
- [ ] 2.4 `SegmentedButton`: `panel` → вкладки с ручной активацией (общий модуль клавиатуры с `Tabs`);
  без `panel` — `radiogroup`; `multiple` — `aria-pressed`. У `Tabs` удалён вид `segmented` [UI-68].
  Проверка — vitest:
  - с `panel` стрелка не переключает, Enter переключает;
  - без `panel` стрелка выбирает;
  - роли — по виду.
- [ ] 2.5 Три места переведены: `TemplatesPage` и `ImportDialog` — с `panel`, `CostViewsBand` — без
  [UI-68]. Проверка:
  - `rg 'variant="segmented"' src/client/src` — пусто;
  - живые прогоны `costs` и `planning-estimates` зелёные, ожидания ролей переписаны.
- [ ] 2.6 Подсказки правил `chip` и `icon-button` в `ui-ratchet-lib.mjs` называют `Chip` (filter, input,
  assist), `SegmentedButton` и `Icon` [UI-68]. Проверка — `ui-ratchet.test.mjs` сверяет текст подсказки.
- [ ] 2.7 `docs/UI.md`: строки «Чип-переключатель» и «Вкладки» — виды чипа и `SegmentedButton`. Если
  выбран цветной контейнер индикатора — строка в разделе отступлений [UI-68]. Проверка — ревью.
- [ ] 2.8 `/design-sync` (D12): превью `Button`, `IconButton`, `Chip` в `.design-sync/previews` и README
  дизайн-системы — по новому виду. Проверка — README и превью без `size="sm"` и `tone`.

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (каждый PR).
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста (каждый PR).
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный: `Button.tsx` с видами не перешагнул 500 строк кода, иначе `IconButton` — своим файлом.
- [ ] 3.4 `npm run -s test:e2e:ci` — весь список, зелёный (каждый PR; зонтик, 5.1).
- [ ] 3.5 Снимки 5–7 ключевых экранов до и после, обе темы, 1366×768 и 375 px (зонтик, 5.2). Проверка —
  описание PR.
- [ ] 3.6 Оба PR с меткой `version:minor`. В описании — макет, выбранный вариант и ссылка
  (`docs/UI.md`, «Новый экран: порядок», шаг 4).
