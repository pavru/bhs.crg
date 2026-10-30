# Design: действия по MD3

Общая архитектура — `../sharedui-md3-00-epic/design.md`:
- D7 — плотность: кнопкам не применяется;
- D8 — отступления;
- D9 — самоделки как предусловие;
- D10 — три макета;
- D11 — сторожа.

Требования — `specs/sharedui/spec.md` [UI-68]. Числа в спецификации — по MD3 (рекомендация зонтика).
Выбор владельца по трём макетам переносится сюда и в спецификацию через `/opsx:update` (`tasks.md`, 0).

## Context

Сверено по master `b32abb6c` (31.10.2026, после архива всех предусловий 0.2 зонтика); вне тестов.

**`Button`** (`shared/ui/Button.tsx`, общие классы — `buttonClasses.ts`):
- `size` `sm`/`md`/`lg` — `h-8`/`h-9`/`h-10`, у `sm` подпись `text-body-small`;
- `danger` (filled — `bg-error`), `multiline` (`min-h-*`), `loading` (`CircularProgress`);
- filled несёт `shadow-elevation-1` всегда (`FILLED_SHADOW`);
- 449 мест в 162 файлах; `size=` у `Button` и `IconButton` — 381 место в 156 файлах (`sm` 335, `lg` 8,
  `md` 1).

**`IconButton`** (тот же файл): `h-8/9/10`, `danger`. 138 мест в 70 файлах; самоделок нет (уровень
`icon-button` — 0, `sharedui-icon-button-screens`).

**`SplitButton`** ([UI-83], `sharedui-split-button`): обе половины собраны из `buttonClasses.ts` —
основная часть `SIZE.md`, стрелка `HEIGHT.md`. Одно место — `EditorPanel`.

**`Chip`** (`Chip.tsx`):
- `rounded-full border text-body-small`, выбранный — `primary-container` с контуром `primary`;
- свойства `selected`, `tone`, `dashed`, `count`, `onRemove`, `menu`, `role="radio"`;
- 43 места в 28 файлах; `tone` или `dashed` — 11 мест в 9 файлах, вместе с `shared/filter/RowFilterValue`
  и `shared/siteFilter/SitesFilter`;
- чипы условий — `shared/filter/FilterChips`.

**`Tabs variant="segmented"`** — 8 мест в 6 файлах (`sharedui-chip-badge-tabs-screens` добавил пять):
- с панелью (`TabPanel`) — `TemplatesPage`, `ImportDialog`, `LinkPickerModal`, `CostViewsBand` (панель
  затрат ссылается на его идентификатор);
- без панели (`panels={false}`) — `SiteCostsPage` («Суммы», «Срез») и `PrimitiveTypesPage` («Точность»,
  «Базовый тип»), по 2–3 варианта.

`Tabs` — APG tablist с ручной активацией. Почему: уход с вкладки может спросить про несохранённое.

**`PrimaryFab`:** `h-14 rounded-lg bg-secondary-container shadow-elevation-3` — форма и тень уже по
шкале (06), контейнер — `secondary-container` вместо `primary-container`.

**Предшественники:**
- форма и тени — 06;
- текст — 07;
- слой состояния и отключённое — 05;
- знаки и `CircularProgress` — 03.

Здесь их только применяют.

**Макет Г:** кнопка 40/24/16 с иконкой; чип 32/8; `.seg` 40; кнопка-иконка 40/24. Но в строках таблицы
у Г кнопка-иконка — **32** (`table.grid .iconbtn`). Это вопрос к макетам и к В7.

## Goals / Non-Goals

**Goals:**
- Пять компонентов по MD3. Отступления названы.
- Экраны получают новый вид даром: самоделки переведены до 10 (0.2 зонтика).

**Non-Goals:**
- **Поведение** — клавиатура, `aria-*`, договор `Tooltip` у неактивной кнопки — не меняется. Исключение
  одно: выбор значения сегментами становится `radiogroup` (ниже).
- **Toggle button M3 Expressive** — не заводится (зонтик, Non-Goals). Split-кнопка уже есть ([UI-83]);
  здесь она только получает высоту 40 и `label-large` через общие классы `buttonClasses.ts`.
- **Чипы-пометки состояния** — это 12 (`StatusText`). Индикатор состояния счёта — потребитель, экран Г.

## Decisions

### `size` уходит из `Button` (рекомендация В7)

Свойство удаляется из типа, и `tsc -b` находит каждое место. Кодмод снимает `size="sm|md|lg"` у
`Button` и `IconButton`. `IconButton` в `md3-migrate.mjs` — по имени тега, иначе он задел бы `size`
у `Checkbox`.

`ButtonSize`, `HEIGHT` и `SIZE` в `buttonClasses.ts` сводятся к одной высоте. `SplitButton` берёт их
оттуда же и получает 40 px без своей правки: требование [UI-83] «одной высоты с кнопкой того же
варианта» держится общими классами, а не числом в двух местах.

**Отвергнуто — оставить `size` и рисовать все размеры по 40.** Мёртвое свойство обещает плотность,
которой нет, и следующий экран попросит «маленькую кнопку».

Если владелец выберет 32 в строках таблиц и панелях (альтернатива В7), заводится одно `density="dense"`
только для этих мест. Оно уходит в раздел отступлений, а правило храповика ограничивает его
`DataGrid`/`EditGrid` и панелями.

### Виды `IconButton` и переключение

`variant: 'standard' | 'filled' | 'tonal' | 'outlined'`, `selected?: boolean`. При заданном `selected`
кнопка ставит `aria-pressed` и `filled` у `Icon` (03).

Цвета по MD3:

| Вид | Без выбора | Выбранная |
|---|---|---|
| standard | `on-surface-variant` | `primary` |
| filled | `surface-container-highest` и `primary` | `primary` / `on-primary` |
| tonal | `surface-container-highest` | `secondary-container` |
| outlined | контур `outline` | `inverse-surface` |

Это при `selected`. Без `selected` filled — `primary` / `on-primary`.

`danger` остаётся у обоих компонентов (отступление): standard-знак становится `error` при наведении. MD3
так не делает, но удаление в строке без подсказки цвета теряет предупреждение.

### Цель нажатия 48

`::after` с `inset: -4px` под `@media (pointer: coarse)` у `Button`, `IconButton`, `Chip`, сегмента.
Видимый размер и раскладка не меняются.

**Отвергнуто — `min-h-12` на телефоне.** Строки и панели выросли бы на 8 px.

**Отвергнуто — условие по ширине экрана (`< 768 px`).** Цель нужна пальцу, а не узкому окну: планшет с
касанием шире 768 px, а узкое окно с мышью её не требует. Живой прогон на пресете `mobile` эмулирует
касание, так что проверка 1.4 видит `pointer: coarse`.

### `Chip`: три вида вместо тонов

- **`kind`.** `filter` — по умолчанию; при `selected` — переключатель. `input` — при `onRemove`.
  `assist` — новый, вместо `dashed`.
- **`tone`** удаляется. Его 11 мест (с `dashed`, 9 файлов) переходят на `icon` со знаком цвета роли: `iconTone: 'error' |
  'warning'`. Это не контейнер: цвет у знака, а не у чипа.
- **`count`** остаётся текстом после подписи. В MD3 счётчика у чипа нет, но число — часть подписи
  отбора, а не badge.
- **`menu`** — filter chip с замыкающей стрелкой вниз, как у MD3 с меню.
- **`role="radio"`** — как было.

Цветной контейнер индикатора (Г `.ind`, `warning-container`) — не `Chip`. Если владелец выберет его в
макетах, это отдельный вид экрана счёта с записью в разделе отступлений.

### `SegmentedButton` — один компонент, две роли

- **Свойство `panel` (связь с `TabPanel`).** Роли `tablist`/`tab`. Ручная активация — тот же модуль
  клавиатуры, что у `Tabs`, без копии.
- **Без `panel`.** `radiogroup`/`radio` по APG: стрелки двигают выбор. Сегодня четыре места
  (`SiteCostsPage` ×2, `PrimitiveTypesPage` ×2) ставят вкладки с `panels={false}` — `tab` без панели,
  что неверно для диктора. Их выбор — запрос отчёта или значение формы, мгновенный выбор стрелкой
  безопасен.
- **`CostViewsBand` — с `panel`.** Панель затрат ссылается на его идентификатор, взгляд переключает
  содержимое: остаётся вкладками с ручной активацией.
- **`multiple`** — `aria-pressed` у сегментов, на будущее отбора.
- У `Tabs` вида `segmented` больше нет. Правило храповика `tabs` не меняется: роли у `SegmentedButton` —
  в `shared/`.

**Отвергнуто — оставить `Tabs variant="segmented"` и перекрасить.** Получилась бы вкладка в чужой
одежде: выбор значения так и остался бы `tab` без панели.

### FAB

`PrimaryFab` — `bg-primary-container text-on-primary-container rounded-lg shadow-elevation-3`,
подпись `label-large`, при наведении — уровень 4. Форма и тень уже стоят (06); меняются контейнер
(`secondary-container` → `primary-container`), подпись и наведение. Своего `style` нет.

### Сторожа

- Подсказка правила `chip` называет `Chip` (filter, input, assist) и `SegmentedButton`.
- Подсказка `icon-button` — «значок `Icon`» вместо lucide (D11).
- Своего правила для `size=` не нужно: его ловит тип.

## Risks / Trade-offs

- **[Кнопки 32 → 40 у 335 мест: панели и строки выше, колонки действий шире (`fixed-width`)]** → три
  макета на плотных местах до кода. Живые прогоны с размерами правятся в том же PR, ширины колонок —
  перепроверить.
- **[Цель 48 у соседних кнопок-иконок перекрывается]** → `inset: -4px` у двух соседних 40 px при зазоре
  0 даёт перекрытие 8 px. Нажатие достаётся верхней. Зазор в рядах кнопок-иконок — не меньше 4 px; так в
  `RowActionsMenu`, проверить в прогоне.
- **[`radiogroup` меняет клавиатуру четырёх мест]** (`SiteCostsPage`, `PrimitiveTypesPage`) → названо в
  спецификации ([UI-42], [UI-68]). Живые прогоны затрат по стройке и типов переписываются тем же PR.
- **[Отложенный `tone` у 9 файлов]** → их переводит этот же PR 2: тип не даст забыть.

## Migration Plan

- **PR 1:** `Button`, `IconButton`, `buttonClasses.ts` (и с ним `SplitButton`), FAB, кодмод `size`.
- **PR 2:** `Chip`, `FilterChips`, `SegmentedButton` и восемь мест сегментов.

Каждый — с полным списком живых прогонов. Откат — ревёрт PR. До 20 псевдонимы держат цвет.
