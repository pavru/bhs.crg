# Tasks: окна, меню, листы и карточки по MD3

## 0. До кода

- [ ] 0.1 Предусловия сверены: слиты и заархивированы change из задачи 0.2 зонтика
  (`sharedui-icon-button-screens`, `sharedui-chip-badge-tabs-screens`, `sharedui-checkbox-screens`,
  `sharedui-page-menus-radix`) и дети `sharedui-md3-05-state-motion`, `-06-shape-elevation`,
  `-07-typescale` (зонтик, раздел 3). Проверка — их нет в `openspec/changes`, уровни `icon-button`,
  `badge`, `chip`, `native-checkbox` в `ui-baseline.json` — ноль или названное исключение
- [ ] 0.2 Разбор с Дизайнером и Архитектором (новые каркасы `Menu`, `SideSheet`, `BottomSheet`, `Card`,
  правило `hand-card`) — комментарием к задаче, по `docs/UI.md` «Новый экран: порядок». Проверка —
  ссылка на разбор в описании PR 1
- [ ] 0.3 Три макета в Claude Design на одном проекте: окно формы и окно «да / нет» (короткое и
  прокрученное), вопрос над окном и список, свисающий с окна, — обе темы; меню действий строки и меню
  профиля; standard и modal side sheet, bottom sheet на 375 px; три вида `Card`. Три разных решения, а
  не три цвета; макеты планируются до 14.12.2026 или туда, что решено в В12. Проверка — ссылка на
  проект в задаче
- [ ] 0.4 Выбор владельца: вариант макета и решение В8 (крестик в шапке, filled среди действий) записаны
  в зонтик (`tasks.md`, 0.1) с датой. Проверка — у В8 стоят дата и выбор
- [ ] 0.5 Выбор перенесён `/opsx:update sharedui-md3-13-containers`: значения и сценарии дельты
  [UI-75]…[UI-77] — по выбранному макету и В8. Проверка — `openspec validate sharedui-md3-13-containers --strict`

## 1. PR 1: окна

- [ ] 1.1 `Modal`: поверхность `surface-container-high`, радиус 28, возвышение 3 без рамки, заголовок
  `headline-small`, действия справа [UI-75]. Проверка — vitest `Modal.test.tsx`: классы ролей на
  содержимом окна, нет `border` и `--f-shadow`
- [ ] 1.2 Черта под шапкой и над подвалом — только при прокрутке тела, наблюдателем пересечения;
  верна и после смены шага `WindowStep` [UI-75]. Проверка — vitest: без прокрутки черт нет, с прокруткой
  есть; живой прогон `dialogs-smoke`: длинное окно прокручено — черты видны
- [ ] 1.3 Крестик по В8 и выход одним путём; окно во весь экран — шапка top app bar, крестик у ведущего
  края, действие у замыкающего; на телефоне — так же [UI-75]. Проверка — vitest: крестик при набранном
  спрашивает, как Esc; живой прогон на 375 px: окно 512 открыто во весь экран
- [ ] 1.4 Вопрос о несохранённом — basic dialog над окном со scrim; `ConfirmDialog` — тот же вид, 384,
  без крестика [UI-75]. Проверка — vitest `Modal.test.tsx` (вопрос над окном, окно под ним `inert`);
  снимок окна над окном в тёмной теме в описании PR
- [ ] 1.5 Поверхность и заголовок голых `Dialog.Root` (`PurgeDialog`, `PurgeBatchDialog`,
  `InvoicePlaceField`, `TypePicker`) — те же роли, логика закрытия не тронута [UI-75]. Проверка —
  `rg "f-shadow28|border-stroke"` по этим файлам пуст; их тесты зелёные
- [ ] 1.6 Живые прогоны, проверяющие размеры окон и крестик, поправлены тем же PR; весь
  `npm run -s test:e2e:ci` зелёный (зонтик, 5.1). Проверка — описание PR
- [ ] 1.7 `docs/UI.md`: «Окна» — basic dialog и full-screen dialog; крестик — в разделе отступлений
  (D8) без слова «MD3» рядом; таблица «Язык формы» — строка «Окно» [UI-75]. Проверка — ревью PR;
  `cd docs/tools && npm run toc`, если менялись главы

## 2. PR 2: меню, листы, карточки, разделитель

- [ ] 2.1 `shared/ui/Menu` (`Menu`, `MenuItem`, `MenuSeparator`, `MenuLabel`, `dense`): `surface-container`,
  радиус 4, возвышение 2, пункт 48 / 40, знак 24, галочка у отмеченного [UI-76]. Проверка — vitest
  `Menu.test.tsx`: роли поверхности, высота пункта по `dense`, клавиатура Radix (стрелки, Esc)
- [ ] 2.2 На `Menu` переведены `RowActionsMenu`, `SectionMenu`, `AccountMenu`, `WorkspaceSwitcher`, слой
  меню масштаба `ViewerToolbar` и меню экранов (`BaseInstanceChip`, `EstimateCard`,
  `InheritedFieldsPanel`, `RolePicker`, `ExportMenu`); слой `Select` и поповеры — та же поверхность
  [UI-76]. Проверка — тест по исходникам: `DropdownMenu.Content` только в `Menu`; существующие тесты
  меню зелёные
- [ ] 2.3 Меню и список у края окна в тёмной теме различимы без рамки [UI-76]. Проверка — живой прогон
  `shared-ui-smoke` с `data-theme=dark`: список свисает за окно; снимок в описании PR
- [ ] 2.4 `Card` (outlined / filled / elevated, слоты шапки, тела, действий) и `Divider` (`inset`,
  `decorative`) [UI-77]. Проверка — vitest: вид по `variant`, `separator` у `Divider` и его отсутствие у
  `decorative`
- [ ] 2.5 `SideSheet` standard и modal и `BottomSheet` (ручка, жест, scrim, 90 % высоты); modal — «Назад»
  закрывает через `historyLayers`; ниже 600 px modal → bottom sheet, standard → во весь экран [UI-77].
  Проверка — vitest: Esc и «Назад» закрывают, фокус возвращается к якорю; жест вниз за половину высоты
  закрывает
- [ ] 2.6 `RowPanel` таблиц — первый потребитель standard side sheet [UI-77]. Проверка — живой прогон
  `table-view-smoke`: строка открывается в панели, таблица сдвинута, scrim нет
- [ ] 2.7 Правило `hand-card` в `scripts/ui-ratchet-lib.mjs` с фикстурами в `ui-ratchet.test.mjs` и
  уровнем по файлам в `ui-baseline.json`; перечень правил в `CLAUDE.md` и `AGENTS.md` — одним коммитом
  [UI-77]. Проверка — фикстура с карточкой руками краснит правило; `AgentRulesCopyTests` зелёный
- [ ] 2.8 Живые прогоны, проверяющие размеры меню и панели строки, поправлены тем же PR; весь
  `npm run -s test:e2e:ci` зелёный. Проверка — описание PR
- [ ] 2.9 `docs/UI.md`: «Слои над окном» — вид слоя ролями без рамки, `shadow28` уходит из правила;
  «Тени» — уровни у окна, меню, листа; новые абзацы «Листы» и «Карточки» [UI-76] [UI-77]. Проверка —
  ревью PR
- [ ] 2.10 `/design-sync` после слияния PR 2: в дизайн-системе «BHS.CRG UI» есть `Menu`, `Card`,
  `Divider`, `SideSheet`, `BottomSheet`, у `Modal` — новый вид (D12). Проверка — превью компонентов в
  дизайн-системе

## 3. Верификация

- [ ] 3.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.2 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста; `hand-card` — с
  уровнем, не выше заведённого
- [ ] 3.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy"`
  — зелёный (копия правил в `AGENTS.md`)
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный; `Modal.tsx` не вырос
- [ ] 3.5 В описании каждого PR — макет, выбранный вариант и ссылка; снимки 5–7 ключевых экранов до и
  после в обеих темах на 1366×768 и 375 px (зонтик, 5.2); весь `npm run -s test:e2e:ci` зелёный (5.1)
- [ ] 3.6 PR с меткой `version:minor`
