# Tasks: строки списков list-detail по MD3 — общий `ListItem`

## 0. До кода

- [ ] 0.1 Предусловия сверены: слиты и заархивированы `sharedui-md3-05-state-motion`, `-07-typescale`,
  `-10-actions` (`IconButton` для действия строки), `-12-feedback` (`StatusText`, `CountText`),
  `-13-containers` (`RowActionsMenu` на общем `Menu`). `sharedui-md3-14-data` и `-15-shell` — не обязательны:
  14 не трогает строки списков, 15 — только подпанель; с 15 сверяется граница (`panelLink` не меняется
  здесь). Проверка — пять названных в архиве (`openspec/changes/archive`)
- [ ] 0.2 Разбор с Дизайнером и Архитектором (`docs/UI.md` «Новый экран: порядок»: новый компонент
  `shared/ui` и новое правило храповика) — комментарием к задаче. Дизайнеру — перечень `design.md` и вопросы
  Л1–Л5 на экранах типов документов, реестра счетов и редактора шаблонов. Архитектору — Р1–Р3 (слоты,
  действие соседом кнопки, `NavItem` обёрткой) и Р5 (признак правила `list-row`). Макет уже выбран владельцем
  (А, 02.11.2026, проект 2d7f81d4, экран 5) — разбор не переоткрывает выбор. Проверка — ссылка в описании PR 1
- [ ] 0.3 Решения владельца по Л1–Л5 (`design.md`, «Решения владельца, нужные до кода») записаны в зонтик
  (раздел 0.1, рядом с В-решениями) и в `design.md` с датой: Л1 — подпись и ширина колонки, Л2 — высота строки
  в одну строку, Л3 — деревья шаблонов, Л4 — списки выбора в окнах, Л5 — ведущий знак. Если владелец
  просит картинку к Л1 или Л3 — три макета в Claude Design (новый проект «Списки list-detail (md3-18)», три
  решения по оси вопроса, типы документов и редактор шаблонов, обе темы, 1366×768 и 375 px) и выбор по ним.
  Проверка — у каждого Л стоят дата и выбор; ссылка на проект, если макеты делались
- [ ] 0.4 Принятое перенесено `/opsx:update sharedui-md3-18-lists`: сценарии [UI-84] (высота, подпись,
  ширина), перечень и задачи PR 2 (деревья, списки выбора), отступления D8 (`depth`, если Л3 — «в этом
  change»). Проверка — `openspec validate sharedui-md3-18-lists --strict`

## 1. PR 1: `ListItem`, `NavItem` и правило

- [ ] 1.1 `shared/ui/ListItem.tsx` по Р1–Р2 [UI-84]: прямоугольник во всю ширину, отступы 16, `gap` 16; 48 без
  `supporting` и 64 с ним (по Л2); `headline` `body-large` `on-surface`, `supporting` `body-medium`
  `on-surface-variant`, замыкающий текст `label-small`; `selected` — `secondary-container` /
  `on-secondary-container` и `aria-current`; цель — `onClick` или `to`; `action` — соседом цели; слой
  состояния у контейнера, кольцо фокуса у цели; обрезанная подпись — `Tooltip` (Р6). Проверка — новый
  `ListItem.test.tsx`: высоты по классам, `aria-current` только у выбранной, `action` не внутри `button`,
  подсказка только при обрезке
- [ ] 1.2 `NavItem` и `NavSection` на `ListItem` (Р3) [UI-84]: API и поведение прежние — `showDetail` у
  пункта уровня, `filter` оставляет список, шеврон не подсвечивается; пометки и число — в `trailing`, знак —
  в `leading` по Л5. `NavSection` — `title-small` `on-surface-variant`, отступы 16 / 16 / 8. Фон колонки
  `ListDetailShell` — `surface`. Проверка — `listDetailView.test.tsx` и `primitives.test.tsx` зелёные, классы
  переписаны вручную вместе с отрицательными (`not.toContain('bg-primary-container')`)
- [ ] 1.3 Правило `list-row` (Р5) [UI-84]: модуль `scripts/ui-ratchet-lists.mjs` с `listRules(countElements)`,
  подключение одной строкой в `ui-ratchet-lib.mjs`, `dirs: ['features']`, подсказка — `ListItem`; тест
  `scripts/ui-ratchet-lists.test.mjs` — положительные случаи (строка счёта, `typeRow`, `div` дерева) и
  отрицательные (`ListItem`, `NavItem`, `data-[state=open]:`, `hover:` без условия, пометка `span`); уровень в
  `ui-baseline.json` — по факту (`npm run ui:ratchet:update` осознанно). Проверка — `npm run ui:ratchet`
  зелёный, тест модуля зелёный; `FileSizeRatchet` — `ui-ratchet-lib.mjs` не перешагнул 500 строк кода
- [ ] 1.4 `docs/UI.md`: строка «Строка списка — `ListItem`» в таблице «Язык формы» и раздел «Списки
  list-detail» (вид А, роли, действие соседом, `NavItem` — для разделов уровня, правило `list-row`); перечень
  правил храповика в `CLAUDE.md` и `AGENTS.md` — одним коммитом. Проверка — `AgentRulesCopyTests` зелёный
- [ ] 1.5 Живая проверка: на экране типов документов строка выбранного типа — фон `secondary-container`,
  высота не ниже 48; у строки с пояснением (примитивные типы) — не ниже 64 — в `pages-smoke` или
  `routing-smoke`. Проверка — новая проверка зелёная на стенде; весь `npm run -s test:e2e:ci` зелёный
  (`ListDetailShell` на двадцати экранах — правка каркаса, весь список)

## 2. PR 2: строки экранов

- [ ] 2.1 Реестры `costs`: `InvoiceListRow` (обе строки) и строка `WaybillsPage` → `ListItem` с `supporting`;
  черта `border-b` снимается [UI-84]. Проверка — `InvoiceListRow` vitest зелёный; `invoices-smoke`,
  `costs-smoke` зелёные
- [ ] 2.2 Настройки: `DocumentTypesPage`, `PrimitiveTypesPage`, `PrintProfilesPage` (миниатюра — `leading`),
  `RecognitionProfilesPage`, `RolesPage`, `BugReportsPage` → `ListItem` [UI-84]. Проверка — vitest экранов
  зелёный; `types-smoke`, `settings-smoke`, `print-profiles-checks`, `pages-smoke` зелёные
- [ ] 2.3 Наборы и каталог: `FileNavItem` (`DataSetsResource`, чип уровня — в `supporting`), `TypeNavItem`
  (`CatalogResource`, профиль уровня — знак `primary` без заливки) → `ListItem` [UI-84]. Проверка — vitest
  `DataSetsResource` и каталога зелёный; `field-mapping-smoke`, `routing-smoke` зелёные
- [ ] 2.4 Редактор комплекта и шаблонов: разделы реквизитов (`RequisitesTab`), шаблоны генерации
  (`GenerationTab`, `Checkbox` в `leading`, фокус — `selected` вместо `border-l-2`), `TypeBlocksPanel` →
  `ListItem`; деревья `UserLibFileList` и `TemplateSidebar` — по Л3 (`depth` и `action`) [UI-84]. Проверка —
  vitest редактора зелёный; `fields-smoke`, `document-preview-checks`, `pages-smoke` зелёные
- [ ] 2.5 «Добавить раздел» (`ConstructionDetail`) и «Добавить комплект» (`SectionDetail`) — `Button
  variant="text"` со знаком `add` (Р4). Проверка — vitest уровня зелёный; правило `icon-button` не выросло
- [ ] 2.6 Уровень `list-row` опущен тем же PR до остатка вне change (по Л3 и Л4); остаток назван в описании
  PR и в задаче 4.1 зонтика (`sharedui-md3-17-screens`). Проверка — `npm run ui:ratchet` зелёный, в
  `ui-baseline.json` у `list-row` только файлы вне перечня

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
