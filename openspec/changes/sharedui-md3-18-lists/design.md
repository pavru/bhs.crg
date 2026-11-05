# Design: строки списков list-detail по MD3 — общий `ListItem`

Общая архитектура — `../sharedui-md3-00-epic/design.md`: D6 (слой состояния), D7 (плотность: списки),
D8 (отступления), D9 (вид — через `shared/ui`), D10 (макеты), D11 (сторожа), D12 (документы и
дизайн-система). Требования — `specs/sharedui/spec.md` [UI-84]. Мотивация — `proposal.md`.

## Context

Сверено по коду master `c5d45c6e` (02.11.2026), поправлено разбором 0.2 по `54ce24b4` (05.11.2026).

**Каркас.** `ListDetailShell.tsx` держит колонку `w-80` (320; второй размер `w-64` объявлен, но ни один экран
его не передаёт) на `surface` (с #1738, md3-15) с чертой `outline-variant`; стоит в 18 файлах `features/`. Его
`NavItem` — пилюля `h-11 rounded-full`, выбранный `primary-container`, подпись `body-medium`, знак 18 —
узлом `<Icon size={18}>` у всех 14 вызовов. `CountText onContainer` жёстко даёт `on-primary-container`.
`NavItem` умеет больше вида:
показывает карточку на узком месте (`ViewContext.showDetail`, #1355), не подсвечивает «ребёнка» с шевроном,
несёт пометки `progress`, `alert`, `note` и число `CountText`. `NavSection` — `title-small`, `px-3 pt-3 pb-1`.

**Вид по варианту А** (`mock/dt.css` проекта 2d7f81d4, экран 5): колонка 320 на `surface`; строка — `gap
16`, `padding 8px 16px`, не ниже 64; подпись 16/24/400 (`body-large`), вторая строка 14/20
`on-surface-variant` (`body-medium`), замыкающий текст 12/16; выбранная — `secondary-container`, наведение —
8 % `on-surface`; заголовок группы — 14/20/500 `on-surface-variant`, отступы 16 / 16 / 8. Строки в одну
строку в макете нет: 48 — из снятого требования 14 и таблицы D7 («списки 48–52»).

### Перечень

Строки, нарисованные экранами, — `rg` по `ListDetailShell`, `NavItem`, `aria-current` и условной заливке
`bg-(primary|secondary)-container` рядом со `state-layer` в `src/client/src/features`:

| № | Файл | Что | Сегодня |
|---|---|---|---|
| 1 | `shared/ui/ListDetailShell.tsx` — `NavItem` | разделы уровня, панели комплекта; 14 вызовов в 6 файлах (`SetDetail` 6, `ReconciliationsPage` 3, `QualityDocsPage` 2, `scopePanels`, `ConstructionDetail`, `SectionDetail`) | пилюля 44, `primary-container` |
| 2 | `costs/InvoiceListRow.tsx` (две строки: счёт и «ушёл из отбора») | реестр счетов | прямоугольник `py-2`, черта `border-b`; до пяти строчек (номер·дата·сумма / поставщик и значки / место / готовность / скан), `scrollIntoView`, e2e `invoice-row` |
| 3 | `costs/WaybillsPage.tsx` | реестр накладных | как 2 |
| 4 | `datasets/DataSetsResource.tsx` — `FileNavItem` | наборы данных | `rounded-md py-1.5`, две строки, чип уровня |
| 5 | `document-sets/catalog/CatalogResource.tsx` — `TypeNavItem` | типы каталога | `rounded-md h-9` |
| 6 | `settings/DocumentTypesPage.tsx` — `typeRow` и заголовок группы | типы документов | копия `NavItem`; группа — кнопка `aria-expanded` с шевроном, папкой и числом; обёртки `px-2`, `space-y-0.5` |
| 7 | `settings/PrimitiveTypesPage.tsx` — `typeRow` | примитивные типы | `rounded-lg py-2`, две строки, пометка |
| 8 | `settings/PrintProfilesPage.tsx` | профили печати | `rounded-md`, миниатюра листа ведущей |
| 9 | `settings/RecognitionProfilesPage.tsx` | профили распознавания | `rounded-md py-2` |
| 10 | `settings/RolesPage.tsx` | роли | `rounded-md py-2`, пометка «системная» |
| 11 | `settings/BugReportsPage.tsx` | сообщения об ошибках | `rounded-lg py-2`, две строки |
| 12 | `templates/TypeBlocksPanel.tsx` | блоки типов в редакторе шаблонов | прямоугольник `py-1.5` |
| 13 | `document-sets/editor/RequisitesTab.tsx` | разделы реквизитов | копия `NavItem`, знак состояния заполнения |
| 14 | `document-sets/editor/GenerationTab.tsx` | шаблоны генерации | строка — `<label>` с флажком (вся строка переключает участие, #316), фокус чертой `border-l-2` |

Действия в колонке, а не строки: «Добавить раздел» (`ConstructionDetail`) и «Добавить комплект»
(`SectionDetail`) — `<button>` `h-9 rounded-full text-primary`.

Деревья — в этом change (Л3): `templates/UserLibFileList.tsx` — настоящее дерево (отступ 16 на уровень,
папка — неинтерактивный заголовок с «+», действия при наведении); `templates/TemplateSidebar.tsx` —
раскрываемая группа «шаблон → версии», сегодня подсвечены и группа, и версия, действия при наведении
(`opacity-0 group-hover`) — на касании их не видно.

**Вне change**, названы, чтобы не искать снова:
- подпанель — `panelLink`, `PageList`, `WorkspaceNav` (`sharedui-md3-15-shell`, drawer);
- списки выбора в окнах, `role="option"` — `PickerRows`, `InvoicePlaceField`, `RefPickerModal`,
  `BaseCandidatePicker`, `ScopeGroupList`, `InstancePickerModal`, `NewNomenclatureStep`, `LinkPickerModal`
  (Л4);
- списки содержимого карточки — замечания сверки (`ReconciliationsPage`, строка находки), уведомления
  (`NotificationList`), элементы массива (`ComplexFields`), оглавление формы (`fields/SectionRail`).

Оба — новый ребёнок зонтика `sharedui-md3-21-pick-lists` (задача 4.1b; `sharedui-md3-17-screens` закрыт
05.11.2026). Узкие боковые колонки `DataSetsResource` (`w-56`) и `CatalogResource` (`w-48`) режут подпись
сильнее 320 и не имеют узкой раскладки — их ширина вне change, видно на снимках 3.1.

**Пересечения.** `InvoiceListRow` правят ещё `costs-invoice-screen-10-layout` (шапка списка, `switchTo`) и
`core-org-short-name` (имя поставщика в строке): кто сливается вторым, тот и переносит правку на `ListItem`.

## Goals / Non-Goals

**Goals:** одна строка списка в `shared/ui`, вид по А; `NavItem` и все строки перечня — через неё; сторож,
который не даст нарисовать строку руками.

**Non-Goals:**
- Раскладка list-detail (ширина колонки, граница узкого экрана 672, «К списку») — не меняется, кроме фона
  колонки и, по Л1, ширины.
- Клавиатурная навигация стрелками по списку (roving) — нет: сегодня её нет, строки ходят Tab. Понадобится —
  отдельный change.
- Перетаскивание, множественный выбор, виртуализация списка — нет.

## Decisions

### Р1. `ListItem` — слоты, а не варианты экранов

`ListItem` принимает `headline`, `supporting` (вторая строка), `leading` (узел: значок, миниатюра,
`Checkbox`), `trailing` (узел: `StatusText`, `CountText`, шеврон) и `action` (отдельная цель — `IconButton`
или `RowActionsMenu`). Высота выводится из `supporting` минимумом, а не `h-*`: нет — `min-h-12`, есть —
`min-h-16`; многострочная вторая часть (`InvoiceListRow`) растёт. `headline` — узел, полный текст для `title`
— отдельной строкой `title`. Цель строки — `onClick` (кнопка) или `to` (ссылка); `ref` и `data-*`
пробрасываются на цель (`scrollIntoView`, e2e `invoice-row`); `selected` ставит `aria-current` и
`secondary-container`. Интерактивный ведущий элемент (флажок `GenerationTab`) — слот `leadingAction`: сосед
цели перед ней, а не `leading` внутри кнопки.

Альтернатива — перечень вариантов («счёт», «тип», «профиль») — отвергнута: компонент знал бы экраны, и
каждый новый список приносил бы вариант.

### Р2. Действие строки — соседом кнопки, а не внутри

Контейнер строки — `div`; цель строки — `button`/`a` `flex-1` с отступами внутри (у строки `gap-0`, мёртвой
полосы перед действием нет), `action` — после неё в том же ряду. Вложенный интерактив в `<button>` —
недопустимая разметка, а у `UserLibFileList` и `TemplateSidebar` действия уже есть.

Слой — новой утилитой `state-layer-row` в `index.css` (разбор Архитектора 05.11.2026): у `div` с
`state-layer` строка теряет нажатие и фокус — `:focus-visible` случается у кнопки, а `:active` нарочно
гасится, когда нажата вложенная кнопка, то есть сама цель (так уже сломаны `UserLibFileList` и
`TemplateSidebar`). `state-layer-row`: наведение — по `:hover` контейнера; нажатие и фокус — по
`:has(> [data-li-target]:is(:focus-visible, :active))`; кольцо `-inset` — у контейнера по тому же `:has`
(внешнее кольцо срезала бы прокрутка колонки), у действия — его собственное. Растянутый `::after` отвергнут:
спорит с `touch-target::after` у `IconButton`. Действие видно всегда — `opacity-0 group-hover` на касании
прячет его вовсе.

### Р3. `NavItem` — обёртка над `ListItem`

`NavItem` сохраняет свой API (его зовут 14 мест) и поведение: `showDetail` для пункта уровня, `filter`,
шеврон «ребёнка», который не подсвечивается (`selected={false}`, чтобы не протёк `aria-current`). Вид и
разметку даёт `ListItem`. Пометки `progress`, `alert`, `note` и `count` — в `trailing`; `CountText` получает
цвет выбранной строки `on-secondary-container` вместо `on-primary-container`. Одно изменение API: `icon`
становится символом, а не узлом — размер 24 (Л5) задаёт обёртка, а 14 вызовов передают сегодня
`<Icon size={18}>`. Селекторы e2e (`button[aria-current]`, `[data-testid=ld-nav] button`) держатся, пока
цель — `button`; хрупок `rail-checks.mjs` (`getByRole('button').last()`) — любое `action` перехватит.

Альтернатива — снять `NavItem` и перевести 13 мест на `ListItem` — отвергнута: правило «пункт уровня
показывает карточку» живёт в одном месте, и разносить его по экранам — регресс #1355.

### Р4. Действие в колонке — кнопка `text`

«Добавить раздел» и «Добавить комплект» — `Button variant="text"` со знаком `add`, с отступом 16 как у
строк. В MD3 у списка нет строки-действия, а строка с цветом `primary` читалась бы как выбранная.

### Р5. Правило `list-row` — своим модулем

`ui-ratchet-lib.mjs` — 479 строк кода при пороге 500 (`FileSizeRatchet`), поэтому правило живёт в
`scripts/ui-ratchet-lists.mjs` с тестом `ui-ratchet-lists.test.mjs`, по образцу `ui-ratchet-cards.mjs`:
модуль экспортирует `listRules(countElements)`, lib подключает его одной строкой. Область — по умолчанию
(вне `shared/ui`, #1469), а не `['features']`. Признак — элемент разметки (`button`, `a`, `div`, `label`,
`li`):
- с `state-layer` или `hover:bg-…`;
- с условной заливкой выбранного: `bg-primary-container` или `bg-secondary-container` в ветке тернарного
  выражения в `className` или вариантом `aria-current:`, `aria-selected:`, `data-[…]:` (кроме
  `data-[state=open]:`). Имя роли — с границами, как `SURFACE_FILL` в `ui-ratchet-cards.mjs`:
  `(?<![\w:/-])bg-(primary|secondary)-container(?![\w/-])` — иначе ловится `hover:bg-primary-container/70`
  (`BaseInstanceChip`).

Не считаются: `data-[state=open]:bg-…` (открытое меню), `hover:bg-primary-container` без условия, заливка
пометок (`span`), строки `DataGrid` (их рисует `shared/ui`). Пропуск, который оставляем: заливка, собранная в
переменную вне `className`. Уровень заводится по факту в PR 1 — прогоном признака около 24 (перечень,
деревья и остаток `ComplexFields` ×3, `RefPickerModal` ×2, `BaseCandidatePicker`, `ScopeGroupList`); PR 2
опускает его до остатка вне change, остаток уходит в ноль в `sharedui-md3-21-pick-lists`. Подсказка — `ListItem` (`shared/ui/ListItem`:
`headline`, `supporting`, `leading`, `trailing`, `action`, `selected`), раздел `docs/UI.md` «Списки
list-detail».

Альтернатива — проверять одно `aria-current` — отвергнута: половина строк перечня его не ставит (счета,
накладные, роли, профили распознавания).

### Р6. Подсказка у обрезанной подписи — нативный `title`

Полный текст обрезанной подписи — нативным `title` у подписи (`truncate`), как у `DetailTitle` и
`FileNavItem`: это законное исключение правила `native-title` (`truncate` + повтор содержимого) и D4
`sharedui-truncated-cell-tooltip` («для строк вне сеток — остаётся»). `Tooltip` у каждой строки длинного
реестра — та цена, которую снимал #1394, и у `Tooltip` со строкой появилось бы `aria-description` — читалка
произнесла бы имя дважды. Решено владельцем 05.11.2026 вместе с Л1 (разбор Архитектора).

### Р7. Раскрываемая группа — `ListGroupHeader` (вариант Б)

Заголовок сворачиваемой группы — `ListGroupHeader` в `shared/ui` рядом с `ListItem`: подзаголовок MD3
(`title-small`, `on-surface-variant`, отступы 16 / 16 / 8) целью не ниже 48, шеврон `expand_less` /
`expand_more` у замыкающего края, `aria-expanded`, число (`CountText`) перед шевроном, `action` — соседом
(«⋮» группы шаблона). Выбранным не бывает. Строки группы — без отступа. Им пользуются `DocumentTypesPage`
(группа типов) и `TemplateSidebar` (шаблон → версии; выбрана только версия, двойная подсветка снимается).
Открытый тип свою группу раскрывает сам, как сегодня. Выбор владельца 05.11.2026 из трёх макетов
(https://claude.ai/design/p/214e220b-c8a7-4f79-8a6b-ed4b8f2ab57c): А — группа без сворачивания и версии в
карточке (тянет переделку шапки редактора), В — строка-родитель с отступом (родитель и ребёнок сливаются в
колонке 256).

### Р8. `GenerationTab` — флажок соседом цели

Строка шаблона генерации — `ListItem` со слотом `leadingAction` (`Checkbox` — участие шаблона) и целью
«выбрать шаблон для параметров» (`selected` вместо черты `border-l-2`). Поведение #316 «щелчок по всей строке
переключает участие» сменяется: флажок — своё, строка — своё. Решение владельца 05.11.2026.

## Решения владельца, нужные до кода (задача 0.3)

**Приняты 05.11.2026** (разбор Дизайнера и Архитектора — задача 0.2): Л1 = (а), полное имя — нативным `title`
(Р6); Л2 = 48; Л3 — в этом change, раскрываемая группа — вариант Б (Р7), `UserLibFileList` — `ListItem` с
`depth`; Л4 — вне change, к `sharedui-md3-21-pick-lists`; Л5 = 24, `icon` у `NavItem` — символом (Р3).
Сверх Л-вопросов — `GenerationTab`, флажок соседом цели (Р8). Записано в зонтик (задача 3.8).

- **Л1. Подпись `body-large` в колонке 320.** Дизайнер предупреждал: подпись 16 режет имена (типы документов,
  наборы данных). Варианты: (а) как в А — `body-large`, многоточие и подсказка, колонка 320; (б) колонка 360
  (ширина list pane MD3) — карточке на 1366 остаётся меньше; (в) подпись `body-medium` — отступление D8.
  Рекомендация — (а): макет выбран с этой подписью, а полное имя в подсказке.
- **Л2. Строка в одну строку — 48 или 56.** В макете А строк в одну строку нет; MD3 даёт 56, таблица D7 и
  снятое требование 14 — 48. Рекомендация — 48 (D7).
- **Л3. Деревья шаблонов** (`UserLibFileList`, `TemplateSidebar`). В MD3 дерева нет. Варианты: в этом change
  — `ListItem` с отступом уровня (`depth`, 16 на уровень; отступление D8); или вне change, к 17.
  Рекомендация — в этом change: иначе редактор шаблонов останется единственным экраном со старой строкой.
- **Л4. Списки выбора в окнах** (`role="option"`, 8 файлов). Это listbox с клавиатурой комбобокса, а не
  колонка list-detail. Рекомендация — не в этом change, к 17 (или своим change, если 17 станет слишком
  большим).
- **Л5. Ведущий знак — 24 (MD3) или 18 (сегодня).** Шкала значков 18 / 20 / 24 (В13). Рекомендация — 24.

Новые три макета нужны, только если владелец не примет Л1 или Л3 без картинки; тогда — проект Claude Design
«Списки list-detail (md3-18)» с тремя решениями по выбранной оси, на экранах типов документов и редактора
шаблонов, в обеих темах.

## Risks / Trade-offs

- [Колонка теряет строки: 44 → 48 и 36–40 → 64] → это выбор владельца (А); на 1366×768 список типов
  документов — около 11 строк вместо 13. Проверка — снимки до и после в PR (зонтик, 5.2).
- [`secondary-container` и наведение на светлой теме различаются слабо — около 1,1 : 1, разбор Дизайнера в
  14] → у выбранной есть ещё `aria-current` и `on-secondary-container`; ведущую черту вариант А отверг.
  В отступления это не записывается: это MD3 как есть.
- [Обёртка `NavItem` меняет разметку: кнопка внутри `div`] → `routing-smoke` и `pages-smoke` ищут
  `button[aria-current]` и `[data-testid=ld-nav] button`: селекторы остаются верными, пока цель строки —
  `button` с `aria-current`; проверяется полным списком живых прогонов.
- [Правило ловит списки вне change] → они остаются уровнем, а не исключением; ноль — в 17.
- [`CatalogResource` подсвечивает профиль уровня цветом `primary`] → это пометка, а не выбор: `leading` со
  знаком роли `primary`, строка без заливки.

## Migration Plan

- **PR 1:** `ListItem`, `ListGroupHeader`, утилита `state-layer-row`, `NavItem` и `NavSection` на них,
  `CountText` на выбранной строке, правило `list-row` с уровнем по факту, `docs/UI.md`, перечень правил в
  `CLAUDE.md` и `AGENTS.md`. Экраны с `NavItem` меняются сами (плюс `icon` символом в 14 вызовах).
- **PR 2:** строки перечня 2–14, действия «Добавить…», группы типов и шаблонов (Р7), дерево `UserLibFileList`,
  `GenerationTab` (Р8); уровень `list-row` опускается тем же PR.
- Откат — revert PR: данных и сервера change не трогает.
