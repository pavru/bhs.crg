# Design: оболочка по MD3

Общая архитектура — `../sharedui-md3-00-epic/design.md`: D5 (значки с заливкой у выбранного), D6
(слой состояния), D7 (плотность списков), D8 (отступления), D10 (три макета), D12 (документы и
дизайн-система). Раскладка оболочки — решение эпика #1428 (вариант B), она не меняется. Требования —
`specs/sharedui/spec.md`.

## Context

Сверено по коду master `e53db2b6` (03.11.2026, разбор Дизайнера и Архитектора — tasks.md 0.2).

Дети 03–14 часть оболочки уже перевели:
- рейл — индикатор 56×32 `secondary-container` и `<Icon filled>` у выбранного (`SectionPill`); у всех
  пяти разделов в манифесте есть `fill`, `RailSectionDef.icon` типизирован `FilledIconDef`. Нижняя
  навигация — `h-20`, индикатор 64×32, знак 24 с заливкой;
- `panelLink` — `rounded-full`, `secondary-container`, `state-layer`; `MICRO_HEADING` — `title-small` без
  прописных;
- `PrimaryFab` — `h-14 rounded-lg` (16), `primary-container`, `shadow-elevation-3`;
- заголовок-кнопка на телефоне — слой состояния и `data-[state=open]:bg-secondary-container`;
- палитра — `rounded-xl` (28), поле 56, поверхность `LAYER_SURFACE` (`surface-container`, из 13);
- корень оболочки — `bg-surface-container-low`; цели касания 48 — правило [UI-68] (`touch-target`).

Осталось:
- `SectionRail.tsx`: `w-[88px] bg-surface`, знак `wide ? 22 : 20`, подпись `text-body-small` с
  `font-semibold` / `font-medium`, у `aside` — `border-r`. `SectionPanel` там же — `bg-surface border-l`,
  заголовок `text-body-large font-semibold`.
- `panelLink.ts`: `h-10 px-3 text-body-medium`, у своего выбранного — `font-medium`, у чужого (`muted`) — нет.
- `AppShell.tsx`: шапка `h-12 border-b bg-surface`; поиск-дверь `h-10 max-md:h-11 bg-surface-container-low`,
  знак 18, `body-medium`, не шире 640; обёртки рабочей области нет; `PhoneBrand` — ссылка 44; комментарий
  про «рейл 88».
- `PageHeader.tsx`: `text-xl font-semibold`, кнопка-заголовок `min-h-11`.
- `BottomNav.tsx`: `bg-surface border-t`, подпись выбранного жирнее.
- `CommandPalette.tsx`: `top-16` по центру, `backdropFilter: blur(2px)`, знак 18 `text-primary`; телефонной
  раскладки нет — карточка шириной `100% - 2rem`.
- `SearchInput.tsx`: `h-10`, `border-outline`, `bg-surface`, `rounded-sm`, очистка — голый знак 18.
- `ListDetailShell.tsx` и `features/templates/UserLibPanel.tsx`: столбец списка `bg-surface-container-low`
  с `border-r`.
- Живые проверки: `rail-checks` и `workspaces-checks` едут внутри `settings-smoke`,
  `invoice-layout-checks` — внутри `invoices-smoke` (`run-suites` берёт только `*-smoke.mjs`).
  `rail-checks` меряет рейл с подпанелью 329, ждёт `header.height <= 49` и порог 44 у `header-search`;
  селектор `main > header button` ссылку `PhoneBrand` не видит; проверки «подпись без переноса» нет.
  `favorites-checks` завязан на `main > header`.
- vitest вида почти не проверяют: `AppShell` не рендерит ни один тест, тестов `PageList` и `WorkspaceNav`
  нет, `CommandPalette.test.tsx` и `paletteGuard.test.tsx` классов не смотрят; у `PageHeader` есть
  `describe` в `primitives.test.tsx`.
- `phoneLayout.ts` уже MD3 (`BOTTOM_NAV_HEIGHT` 80, `FAB_CLEARANCE` 72), `phoneChrome.ts` размеров не
  содержит. `AppShell` — 250 строк, `CommandPalette` — 208: FileSizeRatchet не грозит.

## Goals / Non-Goals

**Goals:** оболочка говорит ролями MD3: рейл, drawer-пункты, top app bar, search bar и view, navigation
bar, extended FAB; выбранное — заливкой знака и `secondary-container`; рабочая область — своя поверхность
со скруглённым углом.

**Non-Goals:**
- Состав навигации, `navConfig.ts`, пространства, `panelOnlyAt`, «Система» в меню профиля на телефоне —
  без изменений.
- Ширина подпанели 360 (drawer MD3) — нет: 240 решено в #1428.
- Заголовок и действия страницы в полосе (макет В) — нет: это перенос `PageHeader` на 26 страницах,
  раскладка, а не вид; отдельный change с Архитектором.
- Меню раздела и профиля на телефоне как bottom sheet — нет: это новое поведение. Слой меню — из
  `sharedui-md3-13-containers`.
- Фон просмотрщиков (`PdfViewer`, `ImageView`, образ счёта) — `sharedui-md3-16-surfaces`.
- Подсказка у обрезанной подписи подпанели — `sharedui-truncated-cell-tooltip`.
- Поиск по данным в палитре — `shell-05-palette-data-search`; здесь только вид.

## Decisions

- **Выбор владельца 03.11.2026 — макет А «MD3 буквально» целиком** (tasks.md 0.3–0.4): полоса 64, search
  bar 56 не шире 720, заголовок страницы в рабочей области, угол 16, пункт подпанели 48, столбец списка
  `surface` с чертой; на телефоне — полоса 64 со строкой поиска 56. Отступлений D8 нет. Замер макетов:
  строк реестра целиком столько же, сколько при полосе 48 (8 на 768, 6 на ~650 с панелями браузера), —
  высота полосы строк не стоит.
- **Рабочая область** — обёртка вокруг области с FAB (`div.relative.flex-1`, `--fab-clearance`) вместе с
  `OutsideBanner` и `EmailConfirmBanner`: `md:rounded-tl-lg overflow-clip bg-surface`. Не внутри
  `overflow-auto`: там угол уезжал бы с прокруткой, а обёртка стала бы ближайшим контейнером прокрутки для
  липких элементов страницы. Без `transform`, `contain` и `will-change` — иначе поедет `fixed` внутри.
  `header` остаётся прямым потомком `main` (на нём держатся `rail-checks` и `favorites-checks`). Шапка —
  прозрачная, черты у шапки и у `aside` сняты. Угол — только от `md`: на телефоне полоса и содержимое —
  одна поверхность `surface`. Порталы Radix и `DataGrid` (его переменные — на своей обёртке прокрутки)
  обёртку не задевают.
- **Подпанель** — на `surface-container-low` вместе с рейлом: `surface` — только рабочая область. Заголовок
  раздела — `title-medium`; пункт — `h-12 px-4 text-label-large`. «Вне пространства» отличает место —
  раскрываемая группа после своих; цвет и вес их больше не различают (свои по MD3 тоже
  `on-surface-variant`, у `label-large` вес 500 у всех), параметр `muted` у `panelLink` уходит. Длинная
  подпись обрезается многоточием («Запомненные сопоставления» ≈ 200 px из 184).
- **Столбец списка list-detail** — `surface` с чертой `outline-variant` у замыкающего края (`ListDetailShell`,
  `UserLibPanel`): на `surface-container-low` рядом с полосой того же цвета угол не виден, а список
  читается частью рамки.
- **Заливка знака** — уже готова (D5): `<Icon filled>`, `fill` в манифесте у всех разделов. Рейлу — знак 24:
  `wide` у `SectionPill` убирается.
- **Поиск-дверь** — search bar: `h-14 rounded-full bg-surface-container-high`, знак 24, подсказка
  `body-large`, `px-4`, не шире 720. На телефоне — та же высота 56 между знаком экземпляра 48 и аватаром 48,
  полоса 64; заголовок-кнопка — строкой ниже.
- **Палитра — docked search view** — остаётся Radix `Dialog`: `useHistoryLayer` и
  `data-modal-state="transient"` сохраняются (на них держатся закрытие по Ctrl+K и `refusedByDialog`), имя
  поля `Поиск и действия` и роль `dialog` — тоже (их ищут живые прогоны). Якорь — прямоугольник
  `header-search` через ref из `AppShell`: поле на месте строки, левый край — её, ширина — большее из её
  ширины и 560. Дверь, ужатая до значка, — откат на центр под полосой. Порог «во весь экран» — `WIDE` 768, а
  не 600 MD3: иначе выйдет третье состояние оболочки. Поверхность — свой класс
  (`surface-container-high`, уровень 3), не `LAYER_SURFACE`: палитра не меню ([UI-76], `menuClasses.ts`).
  Размытие снимается.
- **`SearchInput`** — 56 на странице везде, в том числе в рядах с чипами (выбор владельца), 40 в окне и
  листе. Место — контекстом слоя: `Modal`, `SideSheet` и `BottomSheet` дают его, `SearchInput` внутри
  берёт 40. `FieldDensityContext` для этого не годится: окна отдают `null`, страницу от окна им не отличить.
  Очистка — `IconButton` XS.
- **Цели касания 48** — уже правило [UI-68]. Дотягиваются поиск в шапке и `PhoneBrand` (оба 44),
  `PageHeader` `min-h-11`; `rail-checks` начинает мерить и ссылки (`main > header a`), порог — 48,
  высота шапки — 64; в `docs/UI.md` снимается отступление 44 и число рейла 88.
- **Микрозаголовок подпанели** — уже `title-small` без прописных; трекинг — по решению В10.
- **Отвергнуто — оставить рейл 88.** Он шире спецификации на 8 px. Подписи разделов («Стройки»,
  «Затраты», «Система» — около 52 px) помещаются в 80; самую длинную проверяет `rail-checks`.
- **Отвергнуто — макеты Б «Тонкая полоса» и В «Заголовок в полосе».** Б даёт те же строки реестра ценой
  пяти отступлений D8 (полоса 48, поиск 40, угол 12, пункт 40, поле палитры 48). В даёт +2 строки на 768 и
  +1 на 650, но переносит `PageHeader` в полосу на 26 страницах — см. Non-Goals.

## Risks / Trade-offs

- [Полоса 64 и поиск 56 на 1366×768] → замер макетов: строк реестра столько же, сколько при 48; снимки
  на 1366×768 в описании PR.
- [Телефон: полоса 64 и поиск в списке 56 — 7 строк списка на 812, без FAB 6] → снимки на 375 и 390 px в
  PR 2; `rail-checks`.
- [Скруглённый угол и полноэкранные экраны (матрица разноски)] → угол только у обёртки содержимого;
  окна `fullScreen` лежат поверх и угла не имеют.
- [Палитра у узкой двери или при смене размера окна] → откат на центр; Esc и «Назад» — `keyboard-smoke`.
- [Подпанель «Система» с пунктами 48 — около 816 px] → прокрутка подпанели, она уже `overflow-y-auto`.
- [Храповик опускается] → `legacy-type` (`PageHeader`, `SectionRail`, `BottomNav`) и `icon-button`
  (`SearchInput`) — база тем же PR, иначе храповик прокрутится назад.

## Migration Plan

Два PR: (1) рабочий стол — рейл, подпанель, шапка, рабочая область, столбец списка, заголовок, палитра,
поиск; (2) телефон — полоса телефона, нижняя навигация, палитра во весь экран, цели касания. Откат — ревёрт
PR.
