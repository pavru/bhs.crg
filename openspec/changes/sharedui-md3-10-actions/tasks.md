# Tasks: действия по MD3

## 0. До кода

- [x] 0.1 Предусловия слиты:
  - зонтик 0.2 — `sharedui-icon-button-screens`, `sharedui-chip-badge-tabs-screens`,
    `sharedui-checkbox-screens`, `sharedui-page-menus-radix` заархивированы;
  - зонтик 2.1, 2.3–2.5 — значки, состояние, форма, текст.

  Проверка — уровни `icon-button` и `chip` в `ui-baseline.json` равны нулю или названы исключением;
  в `openspec/changes` нет этих четырёх каталогов. Сверено 31.10.2026 по master `b32abb6c`: все пять
  change зонтика 0.2 в архиве (последний — #1705), `icon-button`, `chip`, `native-checkbox`,
  `manual-dropdown` — 0.
- [x] 0.2 Разбор с Дизайнером и Архитектором (слой `shared/ui`) — комментарием к задаче (`docs/UI.md`,
  «Новый экран: порядок», шаг 1). Проверка — ссылка на разбор в задаче.
  Сделано 31.10.2026. Сводка — лист «0 Разбор и вопрос» проекта Claude Design
  https://claude.ai/design/p/d79801d4-00b7-4eb6-8ee7-d68506dbe64c?file=00+%D0%A0%D0%B0%D0%B7%D0%B1%D0%BE%D1%80+%D0%B8+%D0%B2%D0%BE%D0%BF%D1%80%D0%BE%D1%81.html.
  Оба независимо рекомендуют (Б): кнопка-иконка 32 (размер XS M3 Expressive, знак 20) только в
  клетках `DataGrid`/`EditGrid` и только при мыши; размер ставит CSS-переменная сетки. Находки,
  меняющие план (своё в `shared/ui`: `RowActionsMenu`, крестик `EditGrid`, `MoveButtons`; проп `icon`
  у `IconButton`; 12 мест `tone`, а не 11; `SitesChip` — filter, не assist; раздела «Отступления» в
  `docs/UI.md` нет; сегменты `TemplatesPage` — вкладки), вносятся через `/opsx:update` в 0.4.
- [x] 0.3 Три макета в Claude Design — разные решения, а не три цвета:
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
  Сделано 31.10.2026, проект «Действия MD3 — кнопки, чипы, сегменты (В7)»:
  - (А) https://claude.ai/design/p/d79801d4-00b7-4eb6-8ee7-d68506dbe64c?file=%D0%90+-+40+%D0%B2%D0%B5%D0%B7%D0%B4%D0%B5.html
  - (Б) https://claude.ai/design/p/d79801d4-00b7-4eb6-8ee7-d68506dbe64c?file=%D0%91+-+XS+32+%D0%B2+%D0%BA%D0%BB%D0%B5%D1%82%D0%BA%D0%B0%D1%85.html
  - (В) https://claude.ai/design/p/d79801d4-00b7-4eb6-8ee7-d68506dbe64c?file=%D0%92+-+dense+32+%D0%B2+%D0%BF%D0%B0%D0%BD%D0%B5%D0%BB%D1%8F%D1%85.html
- [x] 0.4 Выбор владельца (pavru) и решение В7 записаны в зонтике с датой (записано 31.10.2026: Б). Выбор перенесён в `design.md`
  и [UI-68] через `/opsx:update sharedui-md3-10-actions`. Проверка —
  `openspec validate sharedui-md3-10-actions --strict`; в design назван макет и вариант.
  Сделано 31.10.2026: (Б) и находки разбора 0.2 — в design, [UI-68], [UI-42], [UI-44], [UI-46], proposal и
  разделах 1–2. По выбору владельца там же: панель — `Tabs` (вид `segmented` перерисован), значение —
  `SegmentedButton` (только `radiogroup`); `ThemeToggle`, `VariantPicker`, `ChoiceRow` — в PR 2.
- [x] 0.5 После архива `sharedui-chip-badge-tabs-screens` и `sharedui-icon-button-screens` — дельта
  дополнена через `/opsx:update`:
  - MODIFIED [UI-42] — режим внутри панели становится `SegmentedButton`, тонов чипа нет;
  - MODIFIED [UI-46] — если в нём названы размеры кнопки-иконки.

  Проверка — `openspec validate … --strict`; в дельте нет `Tabs variant="segmented"` как правила.
  Сделано 31.10.2026: MODIFIED [UI-42] (оба), [UI-44] (`SegmentedButton` с `panel` — тоже вкладки),
  [UI-46] (40×40); context design и числа задач сверены с master `b32abb6c`.

## 1. PR 1 — кнопки, кнопки-иконки, FAB

- [x] 1.1 Раздел «Отступления» в `docs/UI.md` заведён — до 1.11: на него ссылаются D8, [UI-66], [UI-68].
  Проверка — заголовок есть, ссылки из `openspec/specs/sharedui/spec.md` ведут в него.
- [x] 1.2 `Button`: 40 px, `label-large`, четыре вида по MD3, filled без тени (уровень 1 при
  наведении), знак 18, отступы 24/16/12. `size` удалён из типа. `danger` и `multiline` — отступления
  [UI-68]. `buttonClasses.ts` — единственное место высоты: `HEIGHT`, `MIN_HEIGHT`, `LABEL`, `padding()`,
  `FILLED_HOVER_SHADOW`; свои таблицы `Button.tsx` и `ButtonSize` удалены. `SplitButton` зовёт те же
  классы, у него записано, что тени нет и при наведении [UI-83]. Проверка — vitest `Button` и `SplitButton`:
  - класс высоты один;
  - у filled нет `shadow-elevation-*` без наведения;
  - `danger` filled — на `error`;
  - обе половины `SplitButton` — тот же класс высоты, что у `Button`.
- [x] 1.3 Кодмод `md3-size-migrate.mjs` на синтаксическом дереве снимает `size=` у `Button`, `IconButton`,
  `ModalExit` и `PrimaryActionButton` с разрешением импортов (384 места на `b32abb6c`; три — за
  `ModalExit`). Подключён проходом, как `md3-states-migrate`; отчёт — файл:строка и что снято. В PR —
  отдельным коммитом. Команда — в «Перевод: как обновить ветку» `docs/UI.md` [UI-68]. Проверка:
  - `npx tsc -b` без ошибок;
  - тест кодмода на образцах (атрибут на своей строке, после `icon={<Icon … />}`, за `ModalExit`).
- [x] 1.4 `IconButton`: 40×40, знак 24, `variant` (standard, filled, tonal, outlined), `selected` →
  `aria-pressed` и `filled` у знака; `danger` — отступление [UI-68]. Знак — свойство `icon: IconDef`,
  `children` удалён; `selected` типом требует `FilledIconDef`; `loading` и `iconClassName` (цвет знака
  ролью). Генератор значков даёт заливку `Star` (`PushPin` потребителя не имеет). Кодмод тем же модулем
  переводит детей `<Icon>` в `icon` (131 место), прочие — в отчёт и руками.
  Проверка — vitest:
  - `selected` ставит `aria-pressed` и заливку знака;
  - без `selected` атрибута нет;
  - `label` — имя кнопки;
  - `npx tsc -b` без ошибок, у `<IconButton` нет детей.
- [x] 1.5 Размер в клетке (В7 = Б): `dtTd` и `dtTdEdit` в `dataTable.ts` ставят
  `--md-comp-icon-button-size` 32, под `pointer-coarse` — 40; `IconButton` читает её и
  `--md-comp-icon-button-icon` (20/24). Правило храповика `dense-scope` (весь `src/`, уровень 0) [UI-68].
  Проверка — vitest:
  - класс клетки `DataGrid` и `EditGrid` содержит переменную;
  - кнопка-иконка в окне, открытом из клетки, — 40;
  - `ui-ratchet.test.mjs` — правило срабатывает вне `dataTable.ts` и `Button.tsx`.
- [x] 1.6 Самодельные кнопки `shared/ui` → `IconButton`: триггер `RowActionsMenu`, `RemoveButton` в
  `EditGrid`, `MoveButtons`, кнопка `AppShell`. Область правила `icon-button` — `features/` и `shared/` без
  `Button.tsx`, `Chip.tsx` и `SplitButton.tsx`; уровень в `features/` 0, в `shared/ui` — семь накопленных
  с названными детьми эпика (design) [UI-46] [UI-68]. Проверка — `npm run ui:ratchet` без роста, vitest
  `dataTable.test.tsx`: крестик удаления строки — `IconButton` в клетке.
- [x] 1.7 Правило храповика: `h-*`, `w-*`, `size-*`, `min-h-*` в `className` у `Button` и `IconButton`,
  уровень 0. Проверка — `ui-ratchet.test.mjs`; места сегодня (`min-h-11` у трёх `IconButton` и по ширине)
  сняты или переведены.
- [x] 1.8 Цель нажатия 48 px под `pointer: coarse` — утилита `touch-target` (`:where` для `relative`,
  `--_touch-inset` у смежных частей) у `Button`, `IconButton`, половин `SplitButton` [UI-68]. Механизм
  #1434 снят: `max-md:h-11` и `min-h-11` ради цели убраны, `rail-checks` `smallTargets` мерит цель — рамку вместе с
  `::after` (design.md). Проверка — живой прогон `shared-ui` при касании (CDP `Emulation.setTouchEmulationEnabled`;
  сессия держится, пока эмуляция включена, — с отсоединением Chromium её снимает):
  - первым шагом `matchMedia('(pointer: coarse)').matches`, иначе отказ с причиной;
  - касание в 4 px за краем кнопки-иконки нажимает её;
  - касание в 2 px внутри края основной части `SplitButton` — её, а не стрелку.
- [x] 1.9 `PrimaryFab` — extended FAB: `primary-container` (вместо `secondary-container`), форма 16,
  уровень 3, подпись `label-large` [UI-68]. Проверка —
  живой прогон оболочки на телефоне находит FAB 56 px; `style` у него нет.
- [ ] 1.10 Живые прогоны, проверяющие размеры кнопок, правятся тем же PR: `rail-checks`,
  `invoice-layout-checks`, `fields-smoke`, `allocation-matrix-smoke`, `invoice-payment-smoke`,
  `table-view-smoke`, `site-costs-smoke` (свёрнутая строка выше сгиба 768). Перепроверены ширины:
  перенос шапки `InvoicesPage` при 1366, порог 420 px просмотрщика, `ViewerToolbar` (`h-10` с `border-t`).
  Проверка — каждое ожидание названо в PR, весь список зелёный.
- [x] 1.11 `docs/UI.md` [UI-68]:
  - строка «Кнопка» таблицы контролов — 40, четыре вида, одна filled, `IconButton` с видами,
    переключением и 32 в клетках таблиц;
  - строка «`IconButton` (не меньше 32 px)» и совет `Button variant="outlined"` (`size="sm"`) переписаны;
  - в разделе отступлений — `danger`, многострочная кнопка, цель касания по основному указателю.

  Проверка — ревью; в `docs/UI.md` нет `size="sm"` у кнопок.
- [ ] 1.12 `/design-sync` (D12): превью `Button`, `IconButton`, `PrimaryFab` в `.design-sync/previews` и
  README дизайн-системы — по новому виду. Проверка — README и превью без `size="sm"`.
- [x] 1.13 Кнопка внутри поля — место XS (найдено живым прогоном, выбор владельца 31.10.2026, design.md
  «Кнопка внутри поля»): класс `ICON_BUTTON_XS` переезжает в `buttonClasses.ts`; его берут `dataTable.ts`,
  крестик «Очистить дату» в `DateInput` и линза `SourceBindingTrigger`. `dense-scope` считает и класс,
  места названы в `skip`. Проверка:
  - vitest — у крестика `DateInput` и у линзы класс места;
  - `ui-ratchet-buttons.test.mjs` — класс вне мест краснеет;
  - `shared-ui` `date-calendar-has-today-and-clear-returns-by-esc` зелёный.

## 2. PR 2 — чипы и сегменты

- [ ] 2.1 `Chip`: 32 px, форма 8, `label-large`. Вид выводится из свойств, тип — объединение без `kind`:
  filter (`selected`; выбранный — `secondary-container` с галочкой 18, без контура; `menu` — стрелка без
  галочки), input (`onRemove`, обязательный `removeLabel`, `menu` запрещён), assist (знак обязателен,
  вместо `dashed`). `tone`, `dashed` и `role="radio"` удалены; `iconTone` типом требует `icon`. `count` —
  как был [UI-68]. Проверка — vitest `Chip`:
  - у выбранного нет контура и есть галочка;
  - `aria-pressed` у переключателя;
  - у крестика — имя из `removeLabel`;
  - тест типов: `iconTone` без `icon` и `menu` с `onRemove` не собираются.
- [ ] 2.2 Места `tone` и `dashed` — 12 в 9 файлах — переведены [UI-68]:
  - `iconTone` у ведущего знака; без знака (`RevisionLines` ×3, `SupplierMatchesDialog`) знак добавлен;
  - `FilterChips` и `RowFilterValue` — `bg-warning-container!` снят, `SearchOff` из `children` в `icon`;
  - «Удалённые записи» в `InvoiceQueueChips` — разные знаки (`Help` и `Warning`);
  - `SitesChip` — filter chip без пунктира, не assist.

  Проверка — `npx tsc -b` без ошибок; у `<Chip` в `src/client/src` нет `tone=`, `dashed` и `className` с `!`;
  vitest `SitesChip` зелёный.
- [ ] 2.3 `shared/filter/FilterChips` — input chip без стрелки; правка условия по нажатию — как была
  [UI-68]. Проверка — живые прогоны `table-chips` и `table-saved-views` зелёные.
- [ ] 2.4 Модуль клавиатуры `shared/ui/rovingFocus.ts` (`useRoving`): фокус у вкладок, фокус и выбор у
  радио; Home/End и пропуск отключённых у обоих. `Tabs` и `useRadioGroup` переходят на него [UI-68].
  Проверка — vitest модуля: стрелка, Home/End, пропуск отключённого, `select: false` не выбирает.
- [ ] 2.5 Вид сегментов — `segmentedClasses.ts`: 40, форма full радиусами крайних сегментов без
  `overflow-hidden`, контур `outline`, выбранный — `secondary-container` с галочкой. `Tabs` вида
  `segmented` перерисован им. `SegmentedButton` (новый) — только `radiogroup`, без `multiple`; цель касания
  смежных сегментов — `--_touch-inset` по вертикали [UI-68]. Проверка:
  - vitest: у `SegmentedButton` роли `radiogroup` / `radio`, стрелка выбирает;
  - vitest: у `Tabs segmented` стрелка не переключает, Enter переключает;
  - живой прогон `shared-ui` (касание): точка в 2 px внутри края сегмента — его.
- [ ] 2.6 Места переведены [UI-68] [UI-42] [UI-44]:
  - `Tabs segmented` остаются — `ImportDialog`, `LinkPickerModal`, `CostViewsBand`;
  - `TemplatesPage` — `Tabs` вида `underline`;
  - `SegmentedButton` — `SiteCostsPage` («Суммы», «Срез»), «Базовый тип» `PrimitiveTypesPage`,
    `ThemeToggle`, `VariantPicker`, `PrintProfileDetail.ChoiceRow` (своя клавиатура уходит);
  - «Базовый тип»: смена не стирает ограничения до сохранения; в правке — текстом;
  - «Точность» — `RadioGroup` (11), если он слит; иначе `SegmentedButton`, и 11 переводит.

  Проверка:
  - `rg 'panels=\{false\}' src/client/src` — пусто;
  - живые прогоны `costs`, `site-costs`, `types`, `planning-estimates`, `dialogs-smoke` и прогон окна
    выбора ссылки зелёные; ожидания ролей переписаны (`tab` → `radio` там, где нет панели);
  - снимок `LinkPickerModal` на 375 px — ряд помещается или берёт вид `underline`.
- [ ] 2.7 Подсказки правил `chip` и `icon-button` в `ui-ratchet-lib.mjs` называют `Chip` (filter, input,
  assist), `SegmentedButton` и `Icon`; новое правило — самодельный ряд сегментов, уровень 0 [UI-68].
  Проверка — `ui-ratchet.test.mjs` сверяет текст подсказки и срабатывание на образце вида `ThemeToggle`.
- [ ] 2.8 `docs/UI.md`: строки «Чип-переключатель» и «Вкладки» — виды чипа, `Tabs segmented` и
  `SegmentedButton`; довод «32 px раздули бы пилюлю» у крестика чипа поправлен. Если выбран цветной
  контейнер индикатора — строка в разделе отступлений [UI-68]. Проверка — ревью.
- [ ] 2.9 `/design-sync` (D12): превью `Chip`, `SegmentedButton`, `Tabs` и README — по новому виду.
  Проверка — README и превью без `tone`.

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный (каждый PR).
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста (каждый PR).
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный: `Button.tsx` с видами не перешагнул 500 строк кода, иначе `IconButton` — своим файлом.
- [ ] 3.4 `npm run -s test:e2e:ci` — весь список, зелёный (каждый PR; зонтик, 5.1).
- [ ] 3.5 Снимки 5–7 ключевых экранов до и после, обе темы, 1366×768 и 375 px (зонтик, 5.2), среди них
  окно подтверждения и ряд условий отбора. Проверка — описание PR.
- [ ] 3.6 Оба PR с меткой `version:minor`. В описании — макет, выбранный вариант и ссылка
  (`docs/UI.md`, «Новый экран: порядок», шаг 4).
