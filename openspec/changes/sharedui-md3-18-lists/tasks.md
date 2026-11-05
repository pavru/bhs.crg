# Tasks: строки списков list-detail по MD3 — общий `ListItem`

## 0. До кода

- [x] 0.1 Предусловия сверены: слиты и заархивированы `sharedui-md3-05-state-motion`, `-07-typescale`,
  `-10-actions` (`IconButton` для действия строки), `-12-feedback` (`StatusText`, `CountText`),
  `-13-containers` (`RowActionsMenu` на общем `Menu`). `sharedui-md3-14-data` и `-15-shell` — не обязательны:
  14 не трогает строки списков, 15 — только подпанель; с 15 сверяется граница (`panelLink` не меняется
  здесь). Проверка — пять названных в архиве (`openspec/changes/archive`)
  Сверено 05.11.2026 по master `54ce24b4`: все пять в архиве, там же 14 (03.11) и 15 (03.11).
- [x] 0.2 Разбор с Дизайнером и Архитектором (`docs/UI.md` «Новый экран: порядок»: новый компонент
  `shared/ui` и новое правило храповика) — комментарием к задаче. Дизайнеру — перечень `design.md` и вопросы
  Л1–Л5 на экранах типов документов, реестра счетов и редактора шаблонов. Архитектору — Р1–Р3 (слоты,
  действие соседом кнопки, `NavItem` обёрткой) и Р5 (признак правила `list-row`). Макет уже выбран владельцем
  (А, 02.11.2026, проект 2d7f81d4, экран 5) — разбор не переоткрывает выбор. Проверка — ссылка в описании PR 1
  Сделано 05.11.2026. Дизайнер (замер по Roboto на 97 типах и 19 наборах стенда): в 320 при `body-large`
  режется 30–37 из 97 (сегодня 19–21), но имена, различимые только хвостом в скобках, не спасают ни 360, ни
  `body-medium` — Л1 (а); хуже узкие боковые колонки (`DataSetsResource` `w-56`, `CatalogResource` `w-48`) —
  видно на снимках 3.1; Л2 48 (ритм с `panelLink` `h-12`); Л5 24, но 14 вызовов `NavItem` передают
  `<Icon size={18}>` — `icon` символом; Л3 — `UserLibFileList` дерево (`depth`), `TemplateSidebar` —
  раскрываемая группа с двойной подсветкой сегодня, заголовок группы `DocumentTypesPage` — тоже; макеты — по
  оси «раскрываемая группа»; Л4 вне change, но `sharedui-md3-17` закрыт — нужен свой дом; `InvoiceListRow` —
  до пяти строчек (высота «не ниже 64, растёт»), пересекается с `costs-invoice-screen-10-layout`;
  `GenerationTab` — две цели в `<label>`; действие при наведении (`opacity-0 group-hover`) на касании невидимо.
  Архитектор: Context устарел — колонка уже `bg-surface` (#1738), `ui-ratchet-lib.mjs` 479 строк кода,
  `NavItem` — 14 вызовов, `ListDetailShell` — 18 файлов; Р2 на `state-layer` не работает (фокус и нажатие —
  у вложенной кнопки, нажатие гасится нарочно) — нужна утилита `state-layer-row` по `:has(> [data-li-target]…)`,
  кольцо `-inset` у контейнера; цель `flex-1`, у строки `gap-0`; `CountText onContainer` жёстко
  `on-primary-container`; `ref` и `data-*` на цель (`scrollIntoView`, `invoice-row`); `selected` у шеврона —
  `false`; `list-row` — 25 срабатываний, ложное `BaseInstanceChip` (`hover:bg-…/70`), обход
  `aria-current:bg-…` надо ловить, область — по умолчанию, а не `['features']` (#1469); уровень около 24; Р6
  — нативный `title` законен (исключение `native-title`, D4 `sharedui-truncated-cell-tooltip`), `Tooltip` у
  каждой строки — цена, снятая #1394; `rail-checks.mjs:297` (`getByRole('button').last()`) хрупок к `action`.
- [x] 0.3 Решения владельца по Л1–Л5 (`design.md`, «Решения владельца, нужные до кода») записаны в зонтик
  (раздел 0.1, рядом с В-решениями) и в `design.md` с датой: Л1 — подпись и ширина колонки, Л2 — высота строки
  в одну строку, Л3 — деревья шаблонов, Л4 — списки выбора в окнах, Л5 — ведущий знак. Если владелец
  просит картинку к Л1 или Л3 — три макета в Claude Design (новый проект «Списки list-detail (md3-18)», три
  решения по оси вопроса, типы документов и редактор шаблонов, обе темы, 1366×768 и 375 px) и выбор по ним.
  Проверка — у каждого Л стоят дата и выбор; ссылка на проект, если макеты делались
  Принято 05.11.2026 (записано в задачу 3.8 зонтика — решения Л живут у ребёнка, а не в 0.1): Л1 = (а) с
  нативным `title`; Л2 = 48; Л5 = 24, `icon` символом; Л3 — в этом change, ось «раскрываемая группа строк» —
  три макета, выбран **Б «подзаголовок сворачивает группу»**
  (https://claude.ai/design/p/214e220b-c8a7-4f79-8a6b-ed4b8f2ab57c); Л4 — вне change, новый ребёнок зонтика
  `sharedui-md3-21-pick-lists` (4.1b); `GenerationTab` — флажок соседом цели.
- [x] 0.4 Принятое перенесено `/opsx:update sharedui-md3-18-lists`: сценарии [UI-84] (высота, подпись,
  ширина), перечень и задачи PR 2 (деревья, списки выбора), отступления D8 (`depth`, если Л3 — «в этом
  change»). Проверка — `openspec validate sharedui-md3-18-lists --strict`
  Сделано 05.11.2026: спецификация — многострочная строка, `title`, раскрываемая группа, число на выбранной,
  флажок соседом, фокус строки с действием, признак `list-row` (варианты и границы имени); `design.md` —
  Context по коду, Р1–Р3, Р5, Р6 переписаны, Р7 (`ListGroupHeader`), Р8 (`GenerationTab`); задачи ниже.
  `depth` — отступление D8 только у `UserLibFileList`. Валидация зелёная.

## 1. PR 1: `ListItem`, `NavItem` и правило

- [x] 1.1 `shared/ui/ListItem.tsx` по Р1–Р2 [UI-84]: прямоугольник во всю ширину, отступы 16, `gap` 16;
  `min-h-12` без `supporting` и `min-h-16` с ним (Л2); `headline` `body-large` `on-surface` с `title` (Р6),
  `supporting` `body-medium` `on-surface-variant` (узел — многострочный растёт), замыкающий текст
  `label-small`; ведущий знак 24 (Л5); `selected` — `secondary-container` / `on-secondary-container` и
  `aria-current`; цель — `onClick` или `to`, `ref` и `data-*` на цель; `leadingAction` и `action` — соседи
  цели; `depth` — 16 на уровень (D8); утилита `state-layer-row` в `index.css` — слой и кольцо `-inset` строки
  по `:has(> [data-li-target]…)`. Проверка — новый `ListItem.test.tsx`: высоты по классам, `aria-current`
  только у выбранной, `action` и `leadingAction` не внутри `button`, `title` у подписи, `ref` на цели
- [x] 1.1a `shared/ui/ListGroupHeader.tsx` по Р7 [UI-84]: подзаголовок `title-small` `on-surface-variant`
  целью не ниже 48, шеврон `expand_less` / `expand_more`, `aria-expanded`, число, `action` соседом; без
  выбора. Проверка — тест: `aria-expanded` меняется, `aria-current` нет никогда, `action` не внутри `button`
- [x] 1.2 `NavItem` и `NavSection` на `ListItem` (Р3) [UI-84]: поведение прежнее — `showDetail` у пункта
  уровня, `filter` оставляет список, шеврон не подсвечивается (`selected={false}`); пометки и число — в
  `trailing`, `CountText` на выбранной — `on-secondary-container`; `icon` — символом 24 в 14 вызовах.
  `NavSection` — `title-small` `on-surface-variant`, отступы 16 / 16 / 8. Проверка — `listDetailView.test.tsx`
  и `primitives.test.tsx` зелёные, классы переписаны вручную вместе с отрицательными
  (`not.toContain('bg-primary-container')`)
- [x] 1.3 Правило `list-row` (Р5) [UI-84]: модуль `scripts/ui-ratchet-lists.mjs` с `listRules(countElements)`,
  подключение одной строкой в `ui-ratchet-lib.mjs`, область по умолчанию (вне `shared/ui`), подсказка —
  `ListItem`; тест `scripts/ui-ratchet-lists.test.mjs` — положительные случаи (строка счёта, `typeRow`, `div`
  дерева, `aria-current:bg-secondary-container`) и отрицательные (`ListItem`, `NavItem`,
  `data-[state=open]:`, `hover:bg-primary-container/70` — `BaseInstanceChip`, пометка `span`); уровень в
  `ui-baseline.json` — по факту, около 24 (`npm run ui:ratchet:update` осознанно). Проверка — `npm run
  ui:ratchet` зелёный, тест модуля зелёный; `FileSizeRatchet` — `ui-ratchet-lib.mjs` не перешагнул 500 строк
- [x] 1.4 `docs/UI.md`: строка «Строка списка — `ListItem`» в таблице «Язык формы» и раздел «Списки
  list-detail» (вид А, роли, действие соседом и видно всегда, `ListGroupHeader`, `NavItem` — для разделов
  уровня, `title` у подписи, правило `list-row`); перечень правил храповика в `CLAUDE.md` и `AGENTS.md` —
  одним коммитом. Проверка — `AgentRulesCopyTests` зелёный
- [ ] 1.5 Живая проверка: на экране уровня (раздел стройки) выбранный пункт `NavItem` — фон
  `secondary-container`, высота не ниже 48, строка во всю ширину `ld-nav` — в `pages-smoke` или
  `routing-smoke`. Проверка — новая проверка зелёная на стенде; весь `npm run -s test:e2e:ci` зелёный
  (`ListDetailShell` в 18 файлах — правка каркаса, весь список)

## 2. PR 2: строки экранов

- [ ] 2.1 Реестры `costs`: `InvoiceListRow` (обе строки, многострочная вторая часть растёт, `ref` для
  `scrollIntoView`, `data-testid=invoice-row` на цели) и строка `WaybillsPage` → `ListItem` с `supporting`;
  черта `border-b` снимается [UI-84]. Порядок с `costs-invoice-screen-10-layout` и `core-org-short-name` —
  кто второй, тот переносит. Проверка — `InvoiceListRow` vitest зелёный; `invoices-smoke`, `costs-smoke`
  зелёные; строка с готовностью и сканом выше 64 и не обрезана
- [ ] 2.2 Настройки: `DocumentTypesPage` (строка — `ListItem`, группа — `ListGroupHeader`, обёртки `px-2` и
  `space-y-0.5` сняты), `PrimitiveTypesPage`, `PrintProfilesPage` (миниатюра — `leading`),
  `RecognitionProfilesPage`, `RolesPage`, `BugReportsPage` → `ListItem` [UI-84]. Проверка — vitest экранов
  зелёный; `types-smoke`, `settings-smoke`, `print-profiles-checks`, `pages-smoke` зелёные; живая проверка:
  строка выбранного типа во всю ширину колонки, 48, `secondary-container`; у примитивного типа с пояснением —
  не ниже 64
- [ ] 2.3 Наборы и каталог: `FileNavItem` (`DataSetsResource`, чип уровня — в `supporting`, `title` вместо
  своего), `TypeNavItem` (`CatalogResource`, профиль уровня — знак `primary` без заливки) → `ListItem`
  [UI-84]. Проверка — vitest `DataSetsResource` и каталога зелёный; `field-mapping-smoke`, `routing-smoke`
  зелёные
- [ ] 2.4 Редактор комплекта и шаблонов [UI-84]: разделы реквизитов (`RequisitesTab`, заголовок «Разделы» —
  `NavSection`), `TypeBlocksPanel` → `ListItem`; шаблоны генерации (`GenerationTab`) — `ListItem` с
  `leadingAction` (`Checkbox` — участие), строка выбирает шаблон для параметров (`selected` вместо
  `border-l-2`), Р8; `TemplateSidebar` — группа `ListGroupHeader` с «⋮», версии `ListItem` с «⋮», выбрана
  только версия (Р7); `UserLibFileList` — `ListItem` с `depth` и `action`, папка — заголовок; действия видны
  без наведения. Проверка — vitest редактора зелёный (сценарий флажка: флажок не меняет выбранный, строка не
  меняет флажок); `fields-smoke`, `document-preview-checks`, `pages-smoke` зелёные; в `TemplateSidebar` ровно
  одна `aria-current`
- [ ] 2.5 «Добавить раздел» (`ConstructionDetail`) и «Добавить комплект» (`SectionDetail`) — `Button
  variant="text"` со знаком `add` (Р4). Проверка — vitest уровня зелёный; правило `icon-button` не выросло
- [ ] 2.6 Уровень `list-row` опущен тем же PR до остатка вне change (Л4: списки выбора и списки карточки);
  остаток назван в описании PR и в задаче 4.1b зонтика (`sharedui-md3-21-pick-lists`). Проверка — `npm run
  ui:ratchet` зелёный, в `ui-baseline.json` у `list-row` только файлы вне перечня

## 3. Внешние носители

- [ ] 3.1 Снимки до и после (зонтик 5.2): типы документов, реестр счетов, набор данных, комплект (колонка
  панелей), редактор шаблонов — обе темы, 1366×768 и 375 px — артефакт со ссылкой в описании PR 2. Проверка —
  ссылка в описании PR
- [ ] 3.2 `/design-sync` после слияния PR 2: `ListItem` в дизайн-системе «BHS.CRG UI», `NavItem` — новый вид.
  Состояние `.design-sync/` — в основной копии (D12). Проверка — отметка в зонтике (3.8)

## 4. Верификация

- [ ] 4.1 `cd src/client && npx tsc -b`, `npm test`, `npm run ui:ratchet`, `npm run lint:ratchet` — зелёные
- [ ] 4.2 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FileSizeRatchet|AgentRulesCopy"`
  — зелёные (`ui-ratchet-lib.mjs` у порога; копия правил агентов совпадает)
- [ ] 4.3 Весь `npm run -s test:e2e:ci` зелёный в каждом PR (зонтик 5.1) — описание PR
- [ ] 4.4 Оба PR — с меткой `version:minor`; `<Version>` не правится

## Workflow follow-up

- Архив `sharedui-md3-18-lists` после слияния PR 2 и `/design-sync`; галка 3.8 в зонтике — тем же архивом.
