# Proposal: оболочка по MD3 — рейл, подпанель, шапка, поиск, заголовок, нижняя навигация

Дочерний change зонтика `sharedui-md3-00-epic`, общая архитектура — `../sharedui-md3-00-epic/design.md`
(D5, D6, D7, D8, D10, D12).

## Why

Оболочка видна на каждом экране, и раскладка у неё уже MD3 (вариант B эпика #1428): рейл разделов,
подпанель, нижняя навигация, extended FAB. Дети 03–14 перевели индикаторы рейла и нижней навигации, заливку
знака у выбранного, пункт подпанели и плавающую кнопку (design.md, Context). Вид остального не тот:
- **рейл** — 88 px, знак 20–22, подпись `body-small` двумя весами;
- **подпанель** — пункты 40, `body-medium`, на `surface` с чертой;
- **шапка** — 48 px с чертой, поиск-дверь 40, знак 18;
- **рабочая область** — без своей поверхности: угла нет, рейл и шапку отделяют черты;
- **заголовок страницы** — 20/600;
- **палитра команд** — по центру экрана с размытием фона;
- **поиск в списках** — поле 40 с рамкой.

Вариант Г показал оболочку MD3: рейл 80 со знаком 24 и заливкой у выбранного, верхняя полоса на
`surface-container-low`, рабочая область `surface` со скруглённым углом, заголовок 22/28/400.

## What Changes

Высоты и вид — по макету А «MD3 буквально» (выбор владельца 03.11.2026, tasks.md 0.3–0.4): отступлений
от MD3 нет.

- **Рейл** (`SectionRail`) — navigation rail:
  - ширина 80, индикатор 56×32 `secondary-container`;
  - знак 24, у выбранного — заливкой (Material Symbols `-fill`, решение В4);
  - подпись `label-medium`, фон `surface-container-low`.
- **Подпанель** — пункты по образцу navigation drawer:
  - фон `surface-container-low` вместе с рейлом, без черты; заголовок раздела — `title-medium`;
  - высота 48 (список, плотность −1…−2, D7), форма full, выбранный — `secondary-container`;
  - подпись `label-large`, микрозаголовок групп — `title-small` без прописных (решение В10);
  - ширина 240 остаётся (решено в #1428), значков в подпанели по-прежнему нет.
- **Шапка** (`AppShell`) — top app bar:
  - высота 64, фон `surface-container-low` без черты, рабочая область — `surface` со скруглённым ведущим
    верхним углом 16 (на рабочем столе);
  - поиск-дверь — search bar 56: форма full, `surface-container-high`, ведущий знак 24, не шире 720.
- **Столбец списка list-detail** (`ListDetailShell`, `UserLibPanel`) — `surface` с чертой у
  замыкающего края вместо `surface-container-low`: иначе он сливается с рамкой, и угол рабочей области не
  виден.
- **Заголовок страницы** (`PageHeader`) — роль `title-large` 22/28/400; кнопка-заголовок на телефоне —
  слой состояния вместо подложки.
- **Телефон:** полоса 64 — знак экземпляра 48, search bar 56, аватар 48; заголовок-кнопка — строкой ниже.
- **Нижняя навигация** (`BottomNav`) — navigation bar 80 на `surface-container` без черты, индикатор
  64×32, знак 24 с заливкой у выбранного, подпись `label-medium`.
- **Палитра команд** (`CommandPalette`) — search view:
  - docked на месте поиска-двери на рабочем столе, во весь экран, если окно меньше 768 px;
  - поле — search bar 56, без размытия фона;
  - поверхность `surface-container-high`, радиус 28.
- **Плавающая кнопка** (`PrimaryFab`) — extended FAB 56, радиус 16, `primary-container`, возвышение 3 —
  уже так; остаётся проверка.
- **`SearchInput`** (поиск в списках) — search bar 56 на странице везде, плотное поле 40 в окне и боковом
  листе.
- Цели касания на телефоне — 48: это уже правило [UI-68]; дотягиваются поиск в шапке и знак экземпляра
  (сейчас 44). Живая проверка `rail-checks` меряет новое число и ссылки шапки.
- `docs/UI.md` «Оболочка: рейл разделов и подпанель» — новые числа и роли.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: оболочка — рейл, подпанель, шапка и поиск, рабочая область и столбец списка, заголовок
  страницы, нижняя навигация, палитра команд, плавающая кнопка [UI-79].

## Impact

- **Клиент:** `shared/ui/AppShell.tsx`, `SectionRail.tsx` (с `SectionPanel`), `WorkspaceNav.tsx`,
  `panelLink.ts`, `PageList.tsx`, `PageHeader.tsx`, `BottomNav.tsx`, `PrimaryFab.tsx`, `SearchInput.tsx`,
  `CommandPalette.tsx`, `menuClasses.ts` (палитра — не слой меню), `ListDetailShell.tsx`,
  `features/templates/UserLibPanel.tsx`, `SectionMenu.tsx` (слой — из `sharedui-md3-13-containers`).
  `phoneLayout.ts` / `phoneChrome.ts` — не меняются: 80 и 72 там уже MD3.
- **Живые прогоны:** `rail-checks` и `workspaces-checks` (едут внутри `settings-smoke`),
  `invoice-layout-checks` (внутри `invoices-smoke`), `favorites-checks` (`main > header`), `routing-smoke`,
  `keyboard-smoke`; весь список — правка оболочки (память проекта «Правка оболочки — весь список e2e»).
- **Тесты:** новые vitest вида — рейл, `panelLink`, `SearchInput`, палитра; `PageHeader` — в
  `primitives.test.tsx`; `phoneShell.test.tsx`, `railSections.test.ts`, `CommandPalette.test.tsx`,
  `paletteGuard.test.tsx` — зелёные.
- **Храповик интерфейса:** `legacy-type` и `icon-button` опускаются — база тем же PR.
- **Claude Design:** `/design-sync` после слияния.
- **Сервер:** не меняется.
- **PR:** два — рабочий стол (рейл, подпанель, шапка, рабочая область, столбец списка, заголовок, палитра,
  поиск); телефон (полоса телефона, нижняя навигация, палитра во весь экран, заголовок-кнопка, цели
  касания). Оба с меткой `version:minor`.
