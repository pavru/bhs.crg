# Tasks: оболочка по MD3

## 0. До кода

- [ ] 0.1 Предусловия сверены: слиты и заархивированы change задачи 0.2 зонтика, дети
  `sharedui-md3-03-icons` (знаки с заливкой, В4), `-05-state-motion`, `-06-shape-elevation`,
  `-07-typescale` (В10). `sharedui-md3-03-icons` — сверх списка зонтика (раздел 3 называет 2.3–2.5):
  без него нет знака с заливкой у выбранного. `sharedui-md3-13-containers` желателен, но не обязателен:
  до него слой меню раздела и профиля остаётся прежним. Проверка — их нет в `openspec/changes`
- [ ] 0.2 Разбор с Дизайнером (и с Архитектором — обёртка рабочей области в `AppShell`, высоты оболочки
  телефона в `phoneLayout.ts`) — комментарием к задаче (`docs/UI.md` «Новый экран: порядок»). Проверка —
  ссылка в описании PR 1
- [ ] 0.3 Три макета в Claude Design: оболочка на 1366×768 с реестром счетов (рейл, подпанель, шапка,
  поиск), открытая палитра, оболочка на 375 px (нижняя навигация, заголовок-кнопка, FAB), обе темы. Три
  разных решения: высота полосы и поиска — главная развилка. Планируются до 14.12.2026 или по В12.
  Проверка — ссылка на проект в задаче
- [ ] 0.4 Выбор владельца записан в зонтик с датой; перенесён `/opsx:update sharedui-md3-15-shell` —
  высоты шапки и поиска, сценарии [UI-79]; высоты ниже MD3 — в отступления D8. Проверка —
  `openspec validate sharedui-md3-15-shell --strict`

## 1. PR 1: рабочий стол

- [ ] 1.1 `SectionRail`: ширина 80, `surface-container-low`, знак 24, индикатор 56×32
  `secondary-container`, знак с заливкой у выбранного, подпись `label-medium` [UI-79]. Проверка —
  `railSections.test.ts` зелёный; `rail-checks`: ширина 80, длинная подпись без переноса
- [ ] 1.2 Подпанель: `panelLink` — пункт 48 full, выбранный `secondary-container`, `label-large`,
  наведение — слой состояния; `MICRO_HEADING` — `title-small` без прописных [UI-79]. Проверка — vitest
  `PageList`/`WorkspaceNav`; `workspaces-checks` зелёный
- [ ] 1.3 `AppShell`: полоса — top app bar на `surface-container-low` без черты, рабочая область `surface`
  с углом 16; поиск-дверь — search bar по выбору 0.4 [UI-79]. Проверка — vitest `phoneShell.test.tsx` для
  рабочего стола; снимок оболочки в обеих темах в описании PR
- [ ] 1.4 `PageHeader` — `title-large` [UI-79]. Проверка — vitest (класс роли) или `pages-smoke`
- [ ] 1.5 `CommandPalette` — docked search view, поле 56, `surface-container-high` радиус 28, без размытия
  [UI-79]. Проверка — `CommandPalette.test.tsx`, `paletteGuard.test.tsx` зелёные; `keyboard-smoke`: Esc
  и «Назад» закрывают палитру
- [ ] 1.6 `SearchInput` — search bar 56 на странице, 40 в окне и листе, без рамки [UI-79]. Проверка —
  vitest: высота по месту; `invoices-smoke`: поиск в списке работает
- [ ] 1.7 Живые прогоны, проверяющие размеры оболочки (`rail-checks`, `workspaces-checks`,
  `invoice-layout-checks`), поправлены тем же PR; весь `npm run -s test:e2e:ci` зелёный. Проверка —
  описание PR
- [ ] 1.8 `docs/UI.md` «Оболочка: рейл разделов и подпанель» — рейл 80, пункты 48, роли [UI-79].
  Проверка — ревью PR

## 2. PR 2: телефон

- [ ] 2.1 `BottomNav` — navigation bar 80, индикатор 64×32, знак 24 с заливкой у выбранного,
  `label-medium` [UI-79]. Проверка — vitest `phoneShell.test.tsx`
- [ ] 2.2 `PrimaryFab` — extended FAB 56, радиус 16, `primary-container`, возвышение 3 [UI-79]. Проверка —
  `invoices-smoke` на 390 px: FAB над нижней навигацией, не перекрывает последнюю строку списка
- [ ] 2.3 Заголовок-кнопка на телефоне: слой состояния, открытое меню — `secondary-container`; палитра
  на телефоне — во весь экран со стрелкой «Назад» [UI-79]. Проверка — `rail-checks` на 375 px
- [ ] 2.4 Цели касания оболочки — 48: `phoneLayout.ts`, `phoneChrome.ts`, правило `docs/UI.md` (44 → 48)
  и `rail-checks` — тем же PR [UI-79]. Проверка — `rail-checks` на 375 и 390 px зелёный с новым числом
- [ ] 2.5 Весь `npm run -s test:e2e:ci` зелёный (правка оболочки — весь список). Проверка — описание PR
- [ ] 2.6 `/design-sync` после слияния PR 2: `SectionRail`, `BottomNav`, `PrimaryFab`, `PageHeader`,
  `SearchInput` — новый вид в дизайн-системе (D12). Проверка — превью компонентов

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный
- [ ] 3.4 В описании каждого PR — макет, выбранный вариант и ссылка; снимки 5–7 ключевых экранов до и
  после в обеих темах на 1366×768 и 375 px, включая оболочку на телефоне (зонтик, 5.2); весь
  `npm run -s test:e2e:ci` зелёный (5.1)
- [ ] 3.5 PR с меткой `version:minor`
