# Design: списки выбора в окнах и списки карточки по MD3

Общая архитектура — `../sharedui-md3-00-epic/design.md`: D6 (слой состояния), D7 (плотность: списки 48–52),
D8 (отступления), D9 (вид — через `shared/ui`), D10 (макеты), D11 (сторожа), D12 (документы и
дизайн-система). Предшественник — `../archive/2026-11-05-sharedui-md3-18-lists/design.md`: Р1 (слоты
`ListItem`), Р2 (действие — соседом цели, `state-layer-row`), Р5 (признак `list-row`), Р7 (`ListGroupHeader`),
решение Л4. Требования — `specs/sharedui/spec.md` [UI-87], [UI-84]. Мотивация — `proposal.md`.

## Context

Сверено по коду master `470b3998` (06.11.2026, 2.76.0).

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
числу (задача 3.5).

**Вне change**, названо, чтобы не искать снова:
- `Calendar` — список лет `role="listbox"` (`sharedui-md3-16-surfaces`);
- `MenuItem` (`sharedui-md3-13-containers`);
- раскладка окна номенклатуры (`costs-picker-sheet-reflow`);
- раскладка экрана сверки и центра активности.

**Пересечения.** `costs-picker-sheet-reflow` правит `NomenclaturePicker` и называет `PickerRows`.
`work-22-web-entry-ui` и `sharedui-date-input-back-layer` трогают `TypePicker`.
`core-constant-document-fields` и `sharedui-md3-19-requisite-fields` трогают `ComplexFields`. Кто сливается
вторым, тот переносит свою правку на новую строку.

## Goals / Non-Goals

**Goals:** одна строка варианта выбора во всех окнах и слоях; строки списков карточки — на `ListItem`;
сторож, который видит все тринадцать мест перечня; `list-row` — в ноль.

**Non-Goals:**
- Новое поведение клавиатуры. Стрелки, Enter, Home/End и «наведение после покоя» (`selection` в
  `NomenclaturePicker`) остаются как есть. Меняется вид строки и разметка цели.
- Раскладка окон (ширина, лист карточки, узкое место) — не меняется.
- Виртуализация длинных списков, множественный выбор в listbox — нет.

## Decisions

### Р1. Строка варианта — отдельный компонент рядом с `ListItem`

Рабочее имя — `ListOption` (`shared/ui/ListOption.tsx`). Слоты — те же, что у `ListItem`: `headline`,
`supporting`, `icon`/`leading`, `trailing`, `title`. Семантика другая:
- элемент — `div role="option"` с `id` и `aria-selected`, без `tabIndex`. Фокус живёт в поле поиска, а это
  требование listbox с `aria-activedescendant`;
- `active` — заливка `secondary-container` (как у `SuggestBox` и `TypePicker`): слоем фокус не показать,
  фокуса у строки нет;
- нажатие — `onMouseDown` с `preventDefault`, иначе поле теряет фокус, а слой закрывается раньше щелчка
  (так сделано в `SuggestBox` и `InvoicePlaceField`); затем `onChoose`;
- наведение — `state-layer` у самой строки (D6), `onHover` — по `mousemove`, как сегодня;
- `action` — сосед у замыкающего края (шаг вглубь в `InvoicePlaceField`, [UI-87] «Действие внутри
  варианта»), с `tabIndex=-1` и тем же `onMouseDown`.

Общие с `ListItem` отступы, высоты и роли текста выносятся в один помощник `shared/ui`, а не копируются.

Альтернатива — режим `ListItem` (`as="option"`) — оставлена Архитектору (вопрос А1). Против неё: у
`ListItem` цель обязательна и фокусируема, `selected` означает `aria-current`, а `state-layer-row` горит по
фокусу цели. В режиме варианта всё это пришлось бы выключать условиями.

### Р2. Пометки варианта вместо заливок

Подсвеченной строкой бывает только активная (спецификация [UI-87]):
- лучшее совпадение (`PickerOption.best`) — надпись `label-small` роли `primary` над подписью, как
  сегодня, но без `primary-container`;
- «Выбрать» / «›» и `kbd Enter` — в `trailing`;
- архивный вариант — `StatusText` «в архиве», выбор выключен (`aria-disabled`), строка остаётся;
- текущее значение — знаком у замыкающего края. Какой знак (галочка `check` или надпись «сейчас») — вопрос
  П2 к владельцу.

### Р3. Строки карточки — `ListItem`, и ему нужны две формы

Перечень Б ложится на слоты `ListItem` (`leadingAction` — флажок и ручка, `action` — ✎ и 🗑, `supporting` —
провенанс и номер документа). Не хватает двух форм:
- **без цели** — у строки нет «открыть» (ссылки и битые ссылки в `ComplexFields`, найденное в интернете,
  архивные записи `RefPickerArchive`). Строка — `div` без слоя нажатия, действия — соседи. Это Р2 md3-18
  без цели;
- **отмеченная** — флажок `leadingAction` отмечен. Заливка как у выбранной, но без `aria-current`:
  несколько отмеченных строк — не «открытая карточка». Предлагается проп `checked`, отдельный от `selected`.

Тип `Target` у `ListItem` становится необязательным для формы без цели. Разметку решает Архитектор
(вопрос А2). Битая отмеченная строка: заливка `secondary-container` поверх подписи роли `error` или кольцо,
как сегодня. Сегодняшнее кольцо (`selectedRing`) заведено ровно для того, чтобы отметку битой строки было
видно не только по флажку (комментарий у `selectedRing`). Решает разбор Дизайнера.

### Р4. Оглавление формы и уведомления — по ответу владельца

- **Оглавление формы каталога** (13) — это навигация внутри страницы, а не список. Варианты: (а) `ListItem`
  в одну строку 48 с `selected`; (б) пункт drawer MD3 — пилюля 56 на `secondary-container`, как `panelLink`
  подпанели (md3-15). Рекомендация — (б): у оглавления и подпанели одна роль, «куда перейти», и выглядеть
  они должны одинаково. Вопрос П3.
- **Непрочитанное уведомление** (12) — сегодня `secondary-container`, то есть цвет выбранной строки
  [UI-84]. Варианты: (а) точка `primary` и подпись `title-small` на фоне `surface`; (б) заливка
  `surface-container-high`; (в) оставить `secondary-container` отступлением D8. Рекомендация — (а): так
  непрочитанное показывают списки MD3 (почта), и цвет выбора остаётся только за выбором. Вопрос П4.

### Р5. Сторож: `role="option"` и заливка в переменной

Расширяется тот же модуль `scripts/ui-ratchet-lists.mjs` (53 строки). Новое правило не нужно: подсказка
одна, «строка — из `shared/ui`».
- **`role="option"`** у элемента разметки вне `shared/ui` считается всегда, со слоем состояния или без:
  вариант listbox нарисовать руками правильно нельзя (D9). Подсказка называет `ListOption`.
- **Заливка в переменной.** Если в выражении `className` стоит идентификатор, правило ищет его объявление
  `const` в той же функции и проверяет инициализатор так же, как `conditionalFill`. Это закрывает пропуск,
  который md3-18 оставил сознательно (Р5 md3-18: «заливка, собранная в переменную вне `className`»).
  Пропуском остаются переменные из другой функции и из пропов.
- `bg-surface-container` и `bg-surface` как выбранное (6, 13) правило по-прежнему не считает: признак
  «любая условная заливка» поймал бы чипы и вкладки. Эти места закрывает перечень, и проверка конца change —
  по нему (задача 3.5).

Уровень заводится по факту в PR 1. Ожидается около 9: семь нынешних плюс `PickerOption` и
`InvoicePlaceField`. Строки `role="option"` в 3, 4 и 5 уже в счёте, точное число покажет прогон. PR 1 опускает
уровень на окна выбора, PR 2 — в ноль. После нуля `list-row` попадает в `ui-ratchet-empty-level.test.mjs`.
Что сторож видит, проверяют тесты модуля (вопрос А3 — `role` в пропе, `{...rest}`).

### Р6. `SuggestBox` и `TypePicker` — на `ListOption`

Строки вариантов в `shared/ui` переходят на `ListOption`. Иначе в системе останутся три вида варианта: свой
у подсказок поля, свой у выбора типа, свой у окон. Вопрос П5: в этом change или своим (их трогают
`work-22-web-entry-ui` и `sharedui-date-input-back-layer`). Рекомендация — в этом, PR 1: правка — замена
разметки строки, поведения она не меняет.

## Вопросы до кода

**Архитектору** (задача 0.2):
- А1 — `ListOption` отдельным компонентом или режимом `ListItem` (Р1). Общий помощник отступов и ролей
  текста.
- А2 — форма `ListItem` без цели и проп `checked` (Р3): типы, разметка, `state-layer-row` без цели.
- А3 — признаки Р5: `role="option"` через проп и spread, поиск `const` в той же функции, ложные срабатывания
  по всему `src/`, ожидаемый уровень.
- А4 — `ComplexFields.tsx` (981 строка, в `file-size-baseline.json`): переход на `ListItem` обязан не
  растить файл. Если растёт — что вынести (ветки строк массива в свой файл).

**Дизайнеру** (задача 0.2):
- перечень А и Б на экранах окна номенклатуры, окна ссылки, места счёта, поля-массива, сверки и центра
  активности;
- оси для трёх макетов (Р1–Р2) и битая отмеченная строка (Р3).

**Владельцу** (задача 0.4, после макетов):
- П1 — вариант строки выбора (три макета, задача 0.3);
- П2 — знак текущего значения (Р2);
- П3 — оглавление формы (Р4);
- П4 — непрочитанное уведомление (Р4);
- П5 — `SuggestBox` и `TypePicker` в этом change (Р6).

## Risks / Trade-offs

- [Строки в окнах станут выше: 32–40 → 48–64] → в окне выбора номенклатуры на 1366×768 видно меньше
  вариантов. Цену показывают макеты, выбор за владельцем (П1). В макетах — худший случай: двадцать
  вариантов и лучшее совпадение.
- [Активный вариант — `secondary-container`, у отмеченной строки карточки та же заливка] → это разные
  места: в listbox флажков нет, а в карточке нет активного по стрелкам. Смешанного списка в перечне нет.
- [`onMouseDown preventDefault` у варианта мешает выделять текст подписи] → так уже работают `SuggestBox` и
  `InvoicePlaceField`, жалоб нет.
- [Сторож по `role="option"` поймает места вне перечня] → прогон в PR 1 их покажет. Попадут в уровень —
  войдут в перечень этого change.
- [Пересечение с `costs-picker-sheet-reflow` по `PickerRows`] → кто сливается вторым, тот переносит правку.
  Строку здесь меняет только `PickerOption`, раскладку окна — reflow.

## Migration Plan

- **PR 1:** `ListOption` (или режим `ListItem` — по А1), формы `ListItem` без цели и с `checked`, сторож Р5 с
  уровнем по факту, окна выбора 1–6, `SuggestBox` и `TypePicker` (по П5), `docs/UI.md`, перечень правил в
  `CLAUDE.md` и `AGENTS.md`. Уровень `list-row` опускается на окна.
- **PR 2:** списки 7–13, `list-row` в ноль, тест пустого уровня.
- Откат — revert PR: данных и сервера change не трогает.
