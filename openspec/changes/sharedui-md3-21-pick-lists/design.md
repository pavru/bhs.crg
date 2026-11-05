# Design: списки выбора в окнах и списки карточки по MD3

Общая архитектура — `../sharedui-md3-00-epic/design.md`: D6 (слой состояния), D7 (плотность: списки 48–52),
D8 (отступления), D9 (вид — через `shared/ui`), D10 (макеты), D11 (сторожа), D12 (документы и
дизайн-система). Предшественник — `../archive/2026-11-05-sharedui-md3-18-lists/design.md`: Р1 (слоты
`ListItem`), Р2 (действие — соседом цели, `state-layer-row`), Р5 (признак `list-row`), Р7 (`ListGroupHeader`),
решение Л4. Требования — `specs/sharedui/spec.md` [UI-87], [UI-84]. Мотивация — `proposal.md`.

## Context

Сверено по коду master `470b3998` (06.11.2026, 2.76.0) и перепроверено по `af944a5d` (2.77.0, задача 0.1).
Решения раздела 0 (разбор Дизайнера и Архитектора, макеты, выбор владельца 06.11.2026) — в задачах 0.2–0.4
и ниже, в Р1–Р9.

**Что уже есть в `shared/ui`.** У `ListItem` (109 строк) есть слоты `headline`, `supporting`, `icon`/`leading`,
`leadingAction`, `trailing`, `hint`, `action`, `selected`, `depth`. Цель у него обязательна: `onClick` или
`to`. `selected` ставит `aria-current` и `secondary-container`. `ListGroupHeader` — раскрываемый
подзаголовок. В двух местах `shared/ui` уже есть строки вариантов listbox, и вид у них разный:
- `SuggestBox` — `div role="option"`, активный стрелкой залит `secondary-container`. Фокус остаётся в поле,
  поэтому активный выделяет заливка, а не слой: так написано в комментарии.
- `TypePicker` — `li role="option"` с `button` внутри, `rounded-md px-3 py-2`, активный тоже
  `secondary-container`.

Правило `list-row` эти две строки не видит: `shared/ui` вне его области.

### Перечень

**А. Списки выбора — listbox, фокус в поле, активный по `aria-activedescendant`.**

| № | Файл | Что | Сегодня | `list-row` |
|---|---|---|---|---|
| 1 | `costs/PickerRows.tsx` — `PickerOption` | окна номенклатуры (`NomenclaturePicker`), позиции сметы (`PointMatchDialogs`), ресурса (`ResourceMatchDialog`) | `div role="option"`, `rounded-sm px-2 py-1.5`; лучшее — `primary-container` (+ кольцо, если активно), активное — `surface-container`; справа «Выбрать» / «›» и `kbd Enter` | 0 — заливка в переменной `tone` |
| 2 | `costs/InvoicePlaceField.tsx` | место счёта: выпадающий слой поля-комбобокса | `div role="option"`, `rounded-xs`, активный — `primary-container`; текущее — `aria-selected:font-medium`; у стройки — кнопка шага «›» (`tabIndex=-1`, `hover:bg-primary-container`) | 0 — условная заливка без слоя состояния |
| 3 | `document-sets/fields/RefPickerModal.tsx` | ссылка: документы комплекта и поля-источники | `button role="option"`, активный — `secondary-container`, наведение — `hover:bg-primary-container`; знак 18 | 2 |
| 4 | `document-sets/fields/BaseCandidatePicker.tsx` | кандидат базового экземпляра | как 3, группы по уровню — своими заголовками | 1 |
| 5 | `document-sets/fields/ScopeGroupList.tsx` | записи каталога по уровням (в 3 и в 7) | заголовок уровня — `button aria-expanded` `rounded-sm state-layer`; строки — `role="option"` только при `asOptions` | 1 |
| 6 | `costs/NewNomenclatureStep.tsx` — `Row` | «похожие позиции» в шаге новой номенклатуры | `button` / `div` (архивная), `rounded-xs py-1.5`, активный — `surface-container`; `role="option"` нет, стрелки ведёт `selection` окна | 0 — заливка `surface-container` |

`NomenclaturePicker`, `PointMatchDialogs`, `ResourceMatchDialog` держат `role="listbox"` и
`aria-activedescendant` сами, а строку берут у `PickerOption`. Отдельной правки им не нужно, только проверка.

**Б. Списки без listbox — в окне или в карточке; строка — цель, флажок или набор действий.**

| № | Файл | Что | Сегодня | `list-row` |
|---|---|---|---|---|
| 7 | `document-sets/fields/InstancePickerModal.tsx` | «Документы комплекта» под каталогом | `button rounded-sm px-3 py-2 hover:bg-primary-container`, две строки, `DocumentStatusText` | 0 |
| 8 | `document-sets/fields/RefPickerArchive.tsx` | архивные записи (не варианты: нарочно без `role="option"`) и кнопка «оставить» | `li` без цели; кнопка — рамка `outline-variant` и `hover:bg-primary-container` | 0 |
| 9 | `document-sets/editor/LinkPickerModal.tsx` | библиотека документов качества; найденное в интернете | `div hover:bg-primary-container` с кнопкой внутри и `IconButton` «скан»; результат поиска — ссылка + `Button` «В библиотеку», строка не нажимается; обёртка `divide-y` с рамкой | 0 |
| 10 | `document-sets/fields/ComplexFields.tsx` | элементы массива (сводка, ссылка, union-ссылка, битая ссылка) | `div state-layer`, отмеченная флажком — `primary-container`, битая отмеченная — `ring-1 ring-primary`; ведущие — флажок и ручка перетаскивания (`rowChrome`); цель — сводка-кнопка (у ссылок её нет); ✎ и 🗑 — `IconButton` | 3 |
| 11 | `reconciliations/ReconciliationsPage.tsx` — `FindingRow` | находки сверки | `div state-layer group`, флажок «для связывания», статус, подпись, значения; «Разобрать» — `opacity-0 group-hover`; вторая строка — провенанс | 0 |
| 12 | `notifications/NotificationList.tsx` — `NotificationRow` | центр активности | непрочитанное — `bg-secondary-container`, аватар 40, действия — пилюли `secondary-container`; заголовок группы — своя кнопка `aria-expanded` | 0 |
| 13 | `document-sets/fields/SectionRail.tsx` | оглавление формы каталога (`catalog/index.tsx`), `nav` `w-56`, `aria-current` | `rounded-md px-2.5 py-1.5`, выбранный — `bg-surface font-medium`, «не заполнено» — точка `error` | 0 — заливка `surface` |

Не путать с `shared/ui/SectionRail.tsx` (рейл оболочки): его правят `costs-invoice-screen-10-layout` и
`work-04-pinned-sections`, здесь он не меняется.

Счётчик `list-row` (7) покрывает 3, 4, 5, 10. Остальные девять мест он не видит. Ноль на счётчике ещё не
значит «сделано», поэтому сторож расширяется (Р5), а проверка конца change — по перечню, а не по одному
числу (задача 4.4).

**Вне change**, названо, чтобы не искать снова:
- `Calendar` — список лет `role="listbox"` (`sharedui-md3-16-surfaces`);
- `MenuItem` (`sharedui-md3-13-containers`);
- раскладка окна номенклатуры (`costs-picker-sheet-reflow`);
- раскладка экрана сверки и центра активности;
- пункт `Select` (`SelectItem`, 32, галочка справа) — восьмой вид варианта, его назвал Дизайнер. Это пункт
  меню Radix Select, его форма — дело полей выбора (`sharedui-md3-11`), а не строки варианта listbox.

**Пересечения.** `costs-picker-sheet-reflow` правит `NomenclaturePicker` и называет `PickerRows`.
`work-22-web-entry-ui` и `sharedui-date-input-back-layer` трогают `TypePicker`.
`core-constant-document-fields` трогает `ComplexFields` (`sharedui-md3-19-requisite-fields` уже в архиве,
#1778). Кто сливается вторым, тот переносит свою правку на новую строку.

## Goals / Non-Goals

**Goals:** одна строка варианта выбора во всех окнах и слоях; строки списков карточки — на `ListItem`;
сторож, который видит все тринадцать мест перечня; `list-row` — в ноль.

**Non-Goals:**
- Новое поведение клавиатуры. Стрелки, Enter, Home/End и «наведение после покоя» (`selection` в
  `NomenclaturePicker`) остаются как есть. Меняется вид строки и разметка цели. Одно названное исключение —
  Р8: варианты-кнопки окон 3–5 перестают быть остановками Tab.
- Раскладка окон (ширина, лист карточки, узкое место) — не меняется.
- Виртуализация длинных списков, множественный выбор в listbox — нет.

## Decisions

### Р1. Строка варианта — отдельный `ListOption` рядом с `ListItem`, вид — по контейнеру

Ответ А1 (Архитектор): отдельный компонент, не режим `ListItem`. Режим `as="option"` перевернул бы в
`ListItem` три смысла — цель обязательна и фокусируема, `selected` значит `aria-current`, `hint` — `Tooltip`
на фокусируемой цели, — и каждый пришлось бы гасить условием.

**Общий модуль `shared/ui/listRow.tsx`** — его берут оба компонента; отступы и роли текста не копируются:
```ts
type ListLead = { icon?: IconDef; iconTone?: string; leading?: never } | { leading?: ReactNode; icon?: never; iconTone?: never };
export type ListRowContent = ListLead & { headline: ReactNode; title?: string; supporting?: ReactNode; trailing?: ReactNode };
export function listRowBox(o: { form: 'list' | 'menu'; twoLine: boolean; lead: boolean; tail: boolean; depth: number }): string;
export function ListRowBody(p: ListRowContent & { form: 'list' | 'menu'; filled: boolean }): JSX.Element;
```
`ListItem` (`ListItem.tsx:67–98`) переходит на этот модуль без изменения вида.

**`ListOption`:**
```ts
export type ListOptionProps = ListRowContent & {
  id: string;
  /** Вид по контейнеру (П1 = В): окно — 'list', слой под полем — 'menu'. */
  form?: 'list' | 'menu';
  active: boolean;
  /** Текущее значение поля: галочка у края (П2) и aria-selected. */
  current?: boolean;
  /** Архивный вариант: aria-disabled, пометка «в архиве», не выбирается. */
  disabled?: boolean;
  onChoose: () => void;
  onHover?: () => void;
  action?: { icon: IconDef; label: string; onAct: () => void };
  depth?: number;
  'data-testid'?: string;
};
```
- элемент — `div role="option"` с `id`, без `tabIndex`: фокус живёт в поле поиска (listbox с
  `aria-activedescendant`);
- **`aria-selected` — только у `current`** (как у `Calendar` и `InvoicePlaceField`). Сегодня он значит то
  «активный» (`PickerRows`, `RefPickerModal`, `BaseCandidatePicker`, `SuggestBox`, `TypePicker`), то «текущее
  значение». Активный вариант называют `aria-activedescendant` поля и заливка;
- `active` — заливка `secondary-container` / `on-secondary-container`: слоем фокус не показать, фокуса у
  строки нет;
- нажатие — `onMouseDown` с `preventDefault` (иначе поле теряет фокус, а слой закрывается раньше щелчка),
  затем `onChoose`; наведение — `state-layer` у строки (D6), `onHover` — по `mousemove`, как сегодня;
- `action` задан объектом, а не узлом: `tabIndex=-1`, `aria-hidden` и `onMouseDown preventDefault` ставит сам
  компонент (сегодня это держится на памяти автора, `InvoicePlaceField.tsx:174`).

**Формы (П1 = В «по контейнеру», 06.11.2026):**
- `form="list"` (окна: 1, 3, 4, 5, 6 перечня и `TypePicker`) — как `ListItem`: 48 / 64, отступы 16,
  `body-large`, пояснение второй строкой `body-medium`;
- `form="menu"` (слой под полем: 2 перечня и `SuggestBox`) — как пункт `Menu dense`: 40 мышью, 48 на
  касании (`pointer: coarse`), отступы 12, `label-large`, пояснение замыкающим текстом `body-small`,
  перенос длинной подписи разрешён (место счёта, `break-words`).

### Р2. Пометки варианта вместо заливок

Подсвеченной строкой бывает только активная (спецификация [UI-87]):
- лучшее совпадение (`PickerOption.best`) — надпись `label-small` роли `primary` над подписью, без
  `primary-container`; бывает только в форме `list`;
- «Выбрать» из каждой строки уходит (шум, разбор Дизайнера): `kbd Enter` — только у активной; «›» — в
  `trailing` у нелучших вариантов на узком окне (issue #1518 — они открывают карточку);
- архивный вариант — `disabled`: `aria-disabled`, `StatusText` «в архиве», строка остаётся;
- текущее значение — **галочка `check` 24** у замыкающего края, для диктора «сейчас» (П2, 06.11.2026). Ведущий
  слот занят знаком вида в четырёх местах из шести, поэтому знак — у замыкающего края, как в `Select`.

### Р3. Строки карточки — `ListItem`, и ему нужны две формы

Перечень Б ложится на слоты `ListItem` (`leadingAction` — флажок и ручка, `action` — ✎ и 🗑, `supporting` —
провенанс и номер документа). Ответ А2 (Архитектор):
```ts
type Target =
  | { onClick: () => void; to?: never; passive?: never }
  | { to: string; onClick?: never; passive?: never }
  | { passive: true; onClick?: never; to?: never; hint?: never; selected?: never };
// + checked?: boolean — заливка secondary-container без aria-current; fill = selected || checked
```
- **без цели** — явная ветка `passive: true`, а не молча пропущенная цель: обязательность цели там, где она
  есть, сохраняется. Обёртка без `state-layer state-layer-row` (по [UI-84] слоя нажатия нет), тело — `div` с
  тем же `listRowBox`, без `cursor-pointer` и `data-li-target`; `ref` — на тело. Соседи сохраняют
  `data-li-side`;
- **отмеченная** — проп `checked`, отдельный от `selected`: несколько отмеченных строк — не «открытая
  карточка»;
- перетаскивание: `dropTarget` (`ComplexFields.tsx:309–323`) кладёт обработчики и `style` на контейнер —
  API `ListItem` не расширяется, строка оборачивается в `div` с `dropTarget` (такая обёртка уже есть у битой
  строки);
- **битая отмеченная строка — заливкой** (разбор Дизайнера): кольцо `selectedRing` заводили потому, что
  «фон уже занят», а у битой строки фона нет, и кольцо 1 px `primary` похоже на кольцо фокуса. Ошибку несут
  знак и подпись роли `error`; `selectedRing` удаляется. Контраст `error` на `secondary-container` — на
  макете «Списки карточки», обе темы.

### Р4. Оглавление формы и уведомления — выбор владельца

- **Оглавление формы каталога** (13) — **(б) пилюля 48** (П3, 06.11.2026), как `panelLink` подпанели: у
  оглавления и подпанели одна роль, «куда перейти». Высота 48, а не 56: `panelLink.ts:10` — `h-12`.
  Текущий — `secondary-container` с `aria-current`; «не заполнено» — `Badge`. В полноэкранном редакторе
  проверить, не стоит ли оглавление рядом с подпанелью оболочки (два drawer рядом путали бы).
- **Непрочитанное уведомление** (12) — **(а) точка `primary` и вес текста** (П4, 06.11.2026):
  `title-medium` у непрочитанного, `body-large` у прочитанного — кегль один (16/24), строка при прочтении не
  прыгает; фон один. Цвет выбора остаётся только за выбором.

### Р5. Сторож: `role="option"` и заливка в переменной

Расширяется тот же модуль `scripts/ui-ratchet-lists.mjs` (53 строки). Новое правило не нужно. Ответ А3
(Архитектор, прогон по коду):
- **`role="option"`** вне `shared/ui` считается по атрибуту и по литералу в ветке выражения, со слоем
  состояния или без: вариант listbox нарисовать руками правильно нельзя (D9);
- **один счёт на элемент**: «вариант ИЛИ строка», а не сумма признаков;
- **заливка в переменной** — идентификатор в `className` ищется среди `const` той же функции, его
  инициализатор проверяется как `conditionalFill`. Инициализатор подмешивается к тексту `className` ДО
  проверки `STATE` (`ui-ratchet-lists.mjs:38`), иначе признак недостижим. Сегодня новых находок он не даёт
  (`PickerOption.tone` ловится по `role`), но тест модуля обязан его срабатывать;
- названные пропуски: spread из `const`, `role` пропом из другого компонента, переменная из другой функции и
  из пропов;
- подсказка (`instead`) называет `ListOption` и `ListItem`;
- `bg-surface-container` и `bg-surface` как выбранное (6, 13) правило по-прежнему не считает: признак
  «любая условная заливка» поймал бы чипы и вкладки. Эти места закрывает перечень (задача 4.4).

Ложных срабатываний в `features/` и `shared/` прогон не нашёл. **Уровень по факту — 9**: семь нынешних плюс
`PickerRows` и `InvoicePlaceField`. В `shared/ui` нашлось бы ещё 5 (`SuggestBox`, `TypePicker` 2, `Calendar`,
`ListItem`), но область правила прежняя. PR 1 опускает уровень на окна выбора, PR 2 — в ноль. После нуля
`list-row` попадает в `ui-ratchet-empty-level.test.mjs`.

### Р6. `SuggestBox` и `TypePicker` — на `ListOption`

П5 = да, в PR 1 (06.11.2026). Иначе в системе останутся три вида варианта.
- `SuggestBox` — `form="menu"`: сегодня `min-h-8`, станет 40 (48 на касании), как `Menu dense`; в клетке
  `EditGrid` слой вырастет на четверть, а не на половину, как при 48;
- `TypePicker` — окно-палитра, `form="list"`. У него нет `aria-activedescendant`, а кнопка вложена в `option`
  (`TypePicker.tsx:178`): переход на `ListOption` — правка ARIA, она называется в описании PR. Пересечения с
  `work-22-web-entry-ui` и `sharedui-date-input-back-layer` — там же.

### Р7. Группы вариантов: свой listbox у каждой, заголовок вне его

Находка Архитектора: в `RefPickerModal` варианты стоят без `role="listbox"` (поиск `:395–399`, без
`aria-controls`), а заголовки групп `ScopeGroupList:41` — кнопки. Обёрнутые в один listbox, они оказались бы
его запрещёнными детьми. Решение:
- группа — `ListGroupHeader` (кнопка `aria-expanded`, Tab до неё доходит, как сегодня) и за ним свой
  `role="listbox"` с `aria-labelledby` на заголовок;
- поле поиска — `aria-controls` на listbox, где стоит активный вариант, и `aria-activedescendant` на
  активный; стрелки переходят из группы в группу, минуя заголовки и свёрнутые группы (`useScopeGroups`);
- окна без групп держат один listbox, как сейчас.

### Р8. Варианты перестают быть остановками Tab

Варианты-`button` окон 3, 4, 5 сегодня — остановки Tab. Вариант-`div` их убирает: так требует listbox с
`aria-activedescendant` ([UI-87]), но это меняет клавиатуру вопреки Non-Goals и записано здесь как
исключение. Выбор с клавиатуры остаётся — стрелками и Enter в поле поиска. Тесты окон, что ходят Tab'ом по
вариантам, переписываются на стрелки.

### Р9. `ComplexFields` — строки массива в `ArrayItemRow.tsx`

Ответ А4 (Архитектор): четыре ветки строк массива (`:390–477`, около 85 строк кода) на `ListItem` дают −15…−25
строк, вынос в `ArrayItemRow.tsx` (ссылка, битая, union-ссылка, сводка) — ещё около −70. Выносятся в любом
случае, уровень `ComplexFields.tsx` в `file-size-baseline.json` (718) опускается тем же PR. `rowChrome`
остаётся в `ComplexFields`: он замкнут на состояние перетаскивания и передаётся узлом (комментарий `:260` про
компонент внутри рендера).

## Ответы до кода

Вопросы раздела 0 закрыты 06.11.2026 (задачи 0.2–0.4):
- А1 — `ListOption` и `listRow.tsx` (Р1); А2 — ветка `passive`, проп `checked` (Р3); А3 — признаки и уровень 9
  (Р5); А4 — `ArrayItemRow.tsx` (Р9);
- Дизайнер — оси макетов (В «выбор переключателем» заменён на В «по контейнеру»), битая отмеченная — заливкой
  (Р3), «Выбрать» только у активной (Р2);
- владелец — П1 В, П2 галочка, П3 (б), П4 (а), П5 да. Макеты —
  https://claude.ai/artifact/WS1vWJpd1wKUgEdNweodNA.

## Risks / Trade-offs

- [Строки в окнах станут выше: 32–40 → 48–64] → в окне выбора номенклатуры на 1366×768 видно около трёх
  вариантов с пояснением вместо 4–5 (оценка Дизайнера). Цена принята выбором В; в слое под полем строка
  плотная, 40. Замер на стенде — `innerHeight` и высота списка с открытым листом, в описании PR 1.
- [Две формы строки варианта] → правило выбора формы — по контейнеру и одно: окно — `list`, слой — `menu`.
  Записано в `docs/UI.md` (задача 1.9), чтобы новое место не выбирало форму на вкус.
- [Активный вариант — `secondary-container`, у отмеченной строки карточки та же заливка] → это разные
  места: в listbox флажков нет, а в карточке нет активного по стрелкам. Смешанного списка в перечне нет.
- [`onMouseDown preventDefault` у варианта мешает выделять текст подписи] → так уже работают `SuggestBox` и
  `InvoicePlaceField`, жалоб нет.
- [Сторож по `role="option"` поймает места вне перечня] → прогон Архитектора их не нашёл; появятся в PR 1 —
  войдут в перечень этого change.
- [Пересечение с `costs-picker-sheet-reflow` по `PickerRows`] → кто сливается вторым, тот переносит правку.
  Строку здесь меняет только `PickerOption`, раскладку окна — reflow.
- [Смысл `aria-selected` меняется в пяти местах] → тесты, что ждут `aria-selected` у активного, переписываются
  на `aria-activedescendant`; тесты `InvoicePlaceField` уже ждут нового смысла.

## Migration Plan

- **PR 0 (планирование):** раздел 0 — разбор, макеты, выбор и этот перенос; без кода.
- **PR 1:** `listRow.tsx`, `ListOption`, формы `ListItem` (`passive`, `checked`), сторож Р5 с уровнем 9, окна
  выбора 1–6 со структурой групп Р7, `SuggestBox` и `TypePicker`, `docs/UI.md`, перечень правил в `CLAUDE.md`
  и `AGENTS.md`. Уровень `list-row` опускается на окна.
- **PR 2:** списки 7–13, `ArrayItemRow.tsx`, `list-row` в ноль, тест пустого уровня.
- Откат — revert PR: данных и сервера change не трогает.
