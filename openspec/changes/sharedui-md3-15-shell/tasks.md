# Tasks: оболочка по MD3

## 0. До кода

- [x] 0.1 Предусловия сверены: слиты и заархивированы change задачи 0.2 зонтика, дети
  `sharedui-md3-03-icons` (знаки с заливкой, В4), `-05-state-motion`, `-06-shape-elevation`,
  `-07-typescale` (В10). `sharedui-md3-03-icons` — сверх списка зонтика (раздел 3 называет 2.3–2.5):
  без него нет знака с заливкой у выбранного. `sharedui-md3-13-containers` желателен, но не обязателен:
  до него слой меню раздела и профиля остаётся прежним. Проверка — их нет в `openspec/changes`
  Сверено 03.11.2026 по master `e53db2b6`: задача 0.2 зонтика отмечена 31.10.2026; дети 03, 05, 06, 07 —
  в архиве (29.10.2026), желательный 13 — тоже (02.11.2026), как и 14 (03.11.2026). В `openspec/changes`
  из эпика — только 00, 15–18 и 20.
- [x] 0.2 Разбор с Дизайнером (и с Архитектором — обёртка рабочей области в `AppShell`, высоты оболочки
  телефона в `phoneLayout.ts`) — комментарием к задаче (`docs/UI.md` «Новый экран: порядок»). Проверка —
  ссылка в описании PR 1
  Сделано 03.11.2026. Сводка — лист «00 Разбор и вопрос» проекта Claude Design
  https://claude.ai/design/p/70788b18-6b3a-4350-88de-caa087ec88ff?file=00+%D0%A0%D0%B0%D0%B7%D0%B1%D0%BE%D1%80+%D0%B8+%D0%B2%D0%BE%D0%BF%D1%80%D0%BE%D1%81.html.
  Context в design.md отстал: индикаторы рейла и нижней навигации с заливкой знака, `panelLink` на
  `secondary-container` и слое состояния, `MICRO_HEADING` `title-small`, `PrimaryFab` (56, 16,
  `primary-container`, уровень 3), заголовок-кнопка со слоем состояния, палитра 28 / поле 56, цели 48 ([UI-68]) —
  уже сделаны детьми 03–14; пункт «знаки получают `fill`» снимается (у всех пяти разделов он есть). Осталось:
  рейл 88 → 80 и знак 20 → 24 (убрать `wide`), подпись рейла `label-medium` одним весом, пункт подпанели
  40 → 48 `label-large` `px-4`, черты у рейла, подпанели, шапки и нижней навигации, шапка и поиск-дверь, обёртки
  рабочей области нет вовсе, `PageHeader` 20/600 → `title-large`, палитра (якорь, размытие, поверхность, телефон),
  `SearchInput` целиком. Дизайнер: между полосой 48 и 64 на 1366×768 разницы в строках реестра нет — рычаг
  строка заголовка страницы; рекомендует А на рабочем столе и телефон с заголовком в полосе; `SearchInput` 56 —
  только одинокий поиск над списком, 40 в рядах с чипами; признак «вне пространства» цветом больше не работает;
  длинные подписи подпанели обрезаются («Запомненные сопоставления» ≈ 200 из 184); порог палитры — 768, не 600.
  Архитектор: обёртка — вокруг области с FAB вместе с плашками, `md:rounded-tl-lg overflow-clip bg-surface`, без
  `transform`, `header` остаётся прямым потомком `main`; столбец списка `ListDetailShell`, `UserLibPanel` и
  просмотрщики на `surface-container-low` сливаются с рамкой — нужно решение; фон подпанели — `surface-container-low`;
  у палитры свой класс поверхности вместо `LAYER_SURFACE`, якорь — прямоугольник `header-search` через ref,
  `useHistoryLayer` и `data-modal-state` сохранить; `SearchInput` — проп `variant: 'bar' | 'field'`, внутри окна
  принудительно `field`; 44 живёт в `AppShell` (поиск, `PhoneBrand` — ссылка, `rail-checks` её не меряет),
  `PageHeader` `min-h-11`, `rail-checks` (`min: 44`, `header.height > 49`, 329 → 320), `docs/UI.md`; живые проверки
  едут внутри `settings-smoke` и `invoices-smoke`; vitest вида почти не проверяют — нужны новые; храповик опустится
  (`legacy-type`, `icon-button`); FileSizeRatchet не грозит. В перечне файлов не хватает `ListDetailShell.tsx`,
  `SectionPanel`, `menuClasses.ts`, `favorites-checks`.
- [x] 0.3 Три макета в Claude Design: оболочка на 1366×768 с реестром счетов (рейл, подпанель, шапка,
  поиск), открытая палитра, оболочка на 375 px (нижняя навигация, заголовок-кнопка, FAB), обе темы. Три
  разных решения: высота полосы и поиска — главная развилка. Планируются до 14.12.2026 или по В12.
  Проверка — ссылка на проект в задаче
  Сделано 03.11.2026, проект «Оболочка MD3 — рейл, подпанель, шапка, поиск, телефон (md3-15)». Сверх перечня —
  list-detail (угол рядом со столбцом списка), открытое меню раздела на телефоне, счётчики строк и переключатели
  высоты 768 / 650 и 812 / 667. Строк реестра целиком (768 / 650): А 8 / 6, Б 8 / 6, В 10 / 7; телефон 812 —
  строк / без FAB: А 7 / 6, Б 8 / 6, В 8 / 7:
  - (А) MD3 буквально — полоса 64, search bar 56 до 720, заголовок в рабочей области, угол 16, пункт 48,
    столбец списка `surface` с чертой —
    https://claude.ai/design/p/70788b18-6b3a-4350-88de-caa087ec88ff?file=%D0%90+-+MD3+%D0%B1%D1%83%D0%BA%D0%B2%D0%B0%D0%BB%D1%8C%D0%BD%D0%BE.html
  - (Б) Тонкая полоса — полоса 48, поиск 40 до 480, угол 12, пункт 40, столбец списка `surface-container` —
    отступления D8 —
    https://claude.ai/design/p/70788b18-6b3a-4350-88de-caa087ec88ff?file=%D0%91+-+%D0%A2%D0%BE%D0%BD%D0%BA%D0%B0%D1%8F+%D0%BF%D0%BE%D0%BB%D0%BE%D1%81%D0%B0.html
  - (В) Заголовок в полосе — полоса 64 с заголовком и действиями страницы, поиск 48 шириной 360,
    переключатель пространства в подпанели, список list-detail — часть рамки, угол у подробностей —
    https://claude.ai/design/p/70788b18-6b3a-4350-88de-caa087ec88ff?file=%D0%92+-+%D0%97%D0%B0%D0%B3%D0%BE%D0%BB%D0%BE%D0%B2%D0%BE%D0%BA+%D0%B2+%D0%BF%D0%BE%D0%BB%D0%BE%D1%81%D0%B5.html
- [x] 0.4 Выбор владельца записан в зонтик с датой; перенесён `/opsx:update sharedui-md3-15-shell` —
  высоты шапки и поиска, сценарии [UI-79]; высоты ниже MD3 — в отступления D8. Проверка —
  `openspec validate sharedui-md3-15-shell --strict`
  Сделано 03.11.2026. Выбор владельца — **А «MD3 буквально» целиком** (рабочий стол и телефон);
  `SearchInput` — 56 на странице везде (как в proposal), 40 в окне и листе; PR — два. Отступлений D8 нет.
  Без вопроса, по разбору: «вне пространства» различает раскрываемая группа, а не цвет и вес; длинная подпись
  подпанели — многоточием. Перенесено: [UI-79] — полоса 64, поиск 56 не шире 720, угол только от `md`, фон
  подпанели и заголовок раздела, новое требование «Столбец списка list-detail», палитра на месте строки и
  порог 768, поиск в окне 40, цели касания со ссылками шапки; Context в design.md сверен заново, Decisions и
  Risks — по разбору и выбору; задачи 1.1–2.4 привязаны к настоящим тестам, добавлены 1.9 (столбец списка)
  и 1.10 (база храповика). `openspec validate --strict` — зелёный. Записано в зонтике (3.6).

## 1. PR 1: рабочий стол

- [x] 1.1 `SectionRail`: ширина 80, `surface-container-low` без черты у `aside`, знак 24 (`wide` у
  `SectionPill` уходит), индикатор 56×32 `secondary-container`, знак с заливкой у выбранного, подпись
  `label-medium` одним весом [UI-79]. Проверка — новый vitest рейла: `w-20`, знак 24, `filled` у
  выбранного; `railSections.test.ts` зелёный; `rail-checks` (внутри `settings-smoke`): ширина 80, рейл с
  подпанелью 320, самая длинная подпись в одну строку без обрезки — новая проверка
  - Сделано: `w-20 bg-surface-container-low`, знак 24, подпись `text-label-medium whitespace-nowrap` одним цветом. `wide` у `SectionPill` остался, но задаёт только ширину индикатора: у navigation bar он 64×32 ([UI-79]), у рейла 56×32; знак 24 у обоих. Проверки — `shellLook.test.tsx`, `rail-checks` (320, `rail-labels-fit-one-line`).
- [x] 1.2 Подпанель: `SectionPanel` — `surface-container-low` без черты, заголовок раздела `title-medium`;
  `panelLink` — `h-12 px-4 text-label-large`, форма full, выбранный `secondary-container`, наведение — слой
  состояния, параметр `muted` уходит (чужие различает раскрываемая группа `WorkspaceNav`); длинная подпись —
  многоточием; `MICRO_HEADING` — `title-small` без прописных (уже) [UI-79]. Проверка — новый vitest
  `panelLink`: `h-12`, `text-label-large`, у выбранного `bg-secondary-container`; `workspaces-checks` (внутри
  `settings-smoke`) зелёный
  - Сделано: `SectionPanel` без `border-l`, заголовок `text-title-medium`; полоса прокрутки тонкая и кнопка «Скрыть панель» в поле (`-mr-2`) — иначе «Администрирование» (157 px) переносилось посреди слова рядом с полосой; длиннее — перенос по слогам (`hyphens-auto`). Браузер прогонов полос не рисует — `rail-checks` меряет слово против места за вычетом тонкой полосы; `panelLink(isActive)` — `h-12 px-4 text-label-large`, `muted` снят; `MICRO_HEADING` — `px-4` под отступ пункта.
- [x] 1.3 `AppShell`: полоса 64 — top app bar на `surface-container-low`, прозрачная, без черты; обёртка
  рабочей области `md:rounded-tl-lg overflow-clip bg-surface` вокруг области с FAB вместе с `OutsideBanner`
  и `EmailConfirmBanner` (без `transform`, `header` — прямой потомок `main`); поиск-дверь — search bar
  `h-14`, `surface-container-high`, знак 24, `body-large`, `px-4`, не шире 720; комментарий про «рейл 88»
  [UI-79]. Проверка — `rail-checks`: высота шапки 64, у обёртки угол 16, у шапки и рейла нет черты
  (`getComputedStyle`); снимок оболочки в обеих темах в описании PR
  - Сделано: обёртка `data-testid="work-area"`; поиск — `h-14 px-4 gap-4 max-w-180 bg-surface-container-high`, знак 24, `body-large`; телефон до PR 2 — прежний: полоса `h-12 md:h-16`, поиск 44 (ревью #1738: полоса 64 с элементами 44 — промежуточный вид, которого никто не выбирал). Проверка `shell-is-md3-top-app-bar-and-work-area` в `rail-checks`; порог высоты шапки на 390 px — прежние 49, поднимется в 2.3.
- [x] 1.4 `PageHeader` — `title-large` без `font-semibold` [UI-79]. Проверка — `primitives.test.tsx`,
  `describe('PageHeader')`: класс `text-title-large`, нет `font-semibold`
  - Сделано: `text-title-large` у обоих `h1`.
- [x] 1.5 `CommandPalette` — docked search view на месте строки поиска: якорь — прямоугольник
  `header-search` через ref из `AppShell`, левый край — её, ширина — большее из её ширины и 560, у двери,
  ужатой до значка, — откат на центр; поле 56, знак 24 `on-surface`; своя поверхность
  `surface-container-high` уровня 3 вместо `LAYER_SURFACE` (`menuClasses.ts`); без размытия;
  `useHistoryLayer`, `data-modal-state`, имя поля и роль — прежние [UI-79]. Проверка — новый vitest: нет
  `backdropFilter`, класс поверхности; `CommandPalette.test.tsx`, `paletteGuard.test.tsx` зелёные;
  `keyboard-smoke`: Esc и «Назад» закрывают палитру
  - Сделано: место — `paletteDock.ts` (`dockedPlace`, `PALETTE_SURFACE`), меряется layout-эффектом и на `resize`; дверь уже 240 или окно уже 768 — центр. Замер на 1366×768: поле палитры совпало со строкой поиска (577/4, 628).
- [x] 1.6 `SearchInput` — search bar 56 на странице везде, плотное поле 40 в окне и листе (контекст слоя от
  `Modal`, `SideSheet`, `BottomSheet`), форма full, `surface-container-high`, без рамки; очистка —
  `IconButton` XS [UI-79]. Проверка — vitest: 56 на странице, 40 внутри `Modal`; `invoices-smoke`: поиск в
  списке работает
  - Сделано: `InLayerContext` (`layerPlace.ts`) ставит общая граница слоя `LayerBoundary` (вместе со сбросом `FieldDensityContext`) — в `Modal`, `SideSheet` (оба вида и окно-лист), `BottomSheet`; в слое поле — `surface-container-highest`: окно само `surface-container-high`, и поле того же тона без рамки не видно (ревью #1738); отступ текста справа — под кнопку очистки при касании (40); проп `rounded` снят. Очистка — `IconButton` в месте `ICON_BUTTON_XS`: `SearchInput.tsx` вписан в исключения `dense-scope` (скрипт, его тест, `buttonClasses.ts`, `docs/UI.md`) — это кнопка внутри поля.
- [x] 1.7 Живые проверки оболочки поправлены тем же PR: `rail-checks` (329 → 320, высота шапки 64, угол и
  черта) и `workspaces-checks` — внутри `settings-smoke`, `invoice-layout-checks` — внутри `invoices-smoke`,
  `favorites-checks` (`main > header`); весь `npm run -s test:e2e:ci` зелёный. Проверка — описание PR
  - Сделано 03.11.2026: весь `npm run -s test:e2e:ci` на своём стенде (API 5058, клиент 4175, база `bhs_crg_e2e15`) — 21/21, 3 пропуска PDF как в CI; после правки подпанели `settings` и `keyboard` повторены — зелёные. `rail-checks`: 320, `shell-is-md3-top-app-bar-and-work-area`, `rail-labels-fit-one-line`, заголовок подпанели в одну строку; `workspaces-checks`, `invoice-layout-checks`, `favorites-checks` правок не потребовали.
- [x] 1.8 `docs/UI.md` «Оболочка: рейл разделов и подпанель» — рейл 80 (число 88 снято), пункты 48,
  полоса 64, поиск 56, рабочая область с углом 16, столбец списка, роли [UI-79]. Проверка — ревью PR
  - Сделано: абзац «Вид — роли MD3» в «Оболочка: рейл разделов и подпанель», 88 → 80, 640 → 720.
- [x] 1.9 Столбец списка list-detail — `surface` с чертой `outline-variant` у замыкающего края вместо
  `surface-container-low`: `ListDetailShell.tsx`, `features/templates/UserLibPanel.tsx` [UI-79]. Проверка —
  vitest `ListDetailShell`: класс столбца; снимок list-detail в описании PR
  - Сделано: `ListDetailShell` и `UserLibPanel` — `bg-surface`; проверка — `shellLook.test.tsx`.
- [x] 1.10 Храповик интерфейса опустился — база тем же PR: `legacy-type` (`PageHeader`, `SectionRail`),
  `icon-button` (`SearchInput`). Проверка — `npm run ui:ratchet` без просьбы опустить уровень
  - Сделано: `ui-baseline.json` опущен — `icon-button` `SearchInput` 1 → 0, `legacy-type` `PageHeader` 4 → 0, `SectionRail` 3 → 1 (остался `font-semibold` у названия экземпляра — знак бренда, не роль).

## 2. PR 2: телефон

- [ ] 2.1 `BottomNav` — navigation bar 80 на `surface-container` без черты (`border-t` уходит), подпись
  `label-medium` одним весом; индикатор 64×32 и знак 24 с заливкой — уже [UI-79]. Проверка — vitest
  `phoneShell.test.tsx`: класс поверхности, нет `border-t`; база `legacy-type` у `BottomNav` опущена
- [ ] 2.2 `PrimaryFab` — extended FAB 56, радиус 16, `primary-container`, возвышение 3 — уже так, кода нет
  [UI-79]. Проверка — `invoices-smoke` на 390 px: FAB над нижней навигацией, не перекрывает последнюю строку
  списка
- [ ] 2.3 Полоса телефона по макету А: 64 — знак экземпляра 48 (ссылка), search bar 56, аватар 48;
  заголовок-кнопка — строкой ниже, цель 48 (`min-h-11` уходит), слой состояния и `secondary-container` у
  открытого меню — уже; палитра в окне меньше 768 px — во весь экран со стрелкой «Назад» [UI-79]. Проверка —
  `rail-checks` на 375 px; vitest палитры на узком экране
- [ ] 2.4 Цели касания оболочки — 48 (правило [UI-68]): поиск в шапке и `PhoneBrand` дотянуты с 44;
  `rail-checks` — порог 48 и селектор `main > header a`; `docs/UI.md` — отступление 44 снято;
  `phoneLayout.ts` не меняется (80 и 72 уже MD3) [UI-79]. Проверка — `rail-checks` на 375 и 390 px зелёный с
  новым числом
- [ ] 2.5 Весь `npm run -s test:e2e:ci` зелёный (правка оболочки — весь список). Проверка — описание PR
- [ ] 2.6 `/design-sync` после слияния PR 2: `SectionRail`, `BottomNav`, `PrimaryFab`, `PageHeader`,
  `SearchInput` — новый вид в дизайн-системе (D12). Проверка — превью компонентов

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; опустившиеся
  уровни записаны в базу тем же PR
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный
- [ ] 3.4 В описании каждого PR — макет, выбранный вариант и ссылка; снимки 5–7 ключевых экранов до и
  после в обеих темах на 1366×768 и 375 px, включая оболочку на телефоне (зонтик, 5.2); весь
  `npm run -s test:e2e:ci` зелёный (5.1)
- [ ] 3.5 PR с меткой `version:minor`
