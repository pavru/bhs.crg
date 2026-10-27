# Tasks: экраны без самоделок

## 0. До кода

- [ ] 0.1 Предусловия сверены: дети `sharedui-md3-10`…`-16` слиты и заархивированы (зонтик, 4.1 «После
  3.1–3.7»), правила `notice-plaque`, `hand-card`, `legacy-radius`, `legacy-shadow`, `legacy-type` заведены
  с уровнем. Проверка — их нет в `openspec/changes`; пять правил есть в `ui-baseline.json`
- [ ] 0.2 Перечень мест по разделам: из `ui-baseline.json` (пять правил) и отчётов кодмодов 06 и 07 —
  какие места сводятся к готовому компоненту, а какие меняют раскладку. Перечень — комментарием к задаче,
  разбор с Дизайнером. Проверка — у каждого раздела перечень с пометкой «компонент» или «макет»
- [ ] 0.3 Для мест с пометкой «макет»: три макета в Claude Design (до 14.12.2026 или по В12) и выбор
  владельца — до кода своего PR; выбор переносится `/opsx:update`, если меняет сценарии [UI-81].
  Проверка — ссылка на макет и вариант у каждого такого места; `openspec validate sharedui-md3-17-screens --strict`

## 1. PR 1: `settings`, `account`, `auth`, `activity`, `jobs`, `notifications`

- [ ] 1.1 Плашки → `Banner` или supporting text (среди них `SchemaLevelBanner`), карточки → `Card`,
  остатки шкал — ступенью MD3 [UI-81]. Проверка — уровни пяти правил по файлам раздела — ноль;
  `npm test` раздела зелёный
- [ ] 1.2 Живые прогоны раздела (`settings-smoke`, `types-smoke`, `print-profiles-checks`) и весь
  `npm run -s test:e2e:ci` зелёные; снимки экранов раздела до и после в обеих темах. Проверка — описание PR

## 2. PR 2: `datasets`, `templates`, `tables`

- [ ] 2.1 Плашки, карточки, остатки шкал по разделам; панель строки таблиц — уже `SideSheet` (13)
  [UI-81]. Проверка — уровни пяти правил по файлам раздела — ноль
- [ ] 2.2 Живые прогоны (`field-mapping-smoke`, `table-*`) и весь `npm run -s test:e2e:ci` зелёные;
  снимки до и после. Проверка — описание PR

## 3. PR 3: `document-sets`, `quality-docs`, `catalog`, `common-data`

- [ ] 3.1 Плашки и карточки редактора документа и комплекта → `Banner` / `Card`;
  `DocumentPreviewPanel` → `SideSheet`, если раскладка не меняется, иначе по макету [UI-81]. Проверка —
  уровни пяти правил по файлам раздела — ноль; тесты раздела зелёные
- [ ] 3.2 Живые прогоны (`document-preview-checks`, `fields-smoke`, `pages-smoke`) и весь
  `npm run -s test:e2e:ci` зелёные; снимки комплекта документов до и после. Проверка — описание PR

## 4. PR 4: `costs`, `planning`, `periods`, `reconciliations`

- [ ] 4.1 Плашки (`StaleInvoiceNotice`, `CorrectionBar` и др.) → `Banner`, карточки → `Card`,
  `NomenclatureCardPanel` → `SideSheet`. Файлы экрана счёта и строк счёта, ещё открытые в своих change,
  обходятся и доводятся в PR 5 [UI-81]. Проверка — уровни пяти правил по файлам раздела — ноль, кроме
  названных в описании PR
- [ ] 4.2 Живые прогоны (`invoices-smoke`, `invoice-*`, `costs-smoke`, `site-costs-smoke`,
  `planning-estimates-smoke`) и весь `npm run -s test:e2e:ci` зелёные; снимки реестра счетов и счёта до и
  после. Проверка — описание PR

## 5. PR 5: `shared/` и правила в ноль

- [ ] 5.1 Остатки `shared/ui` вне компонентов 10–16 (`EmptyState`, `RereadFailedNotice`,
  `StaleSourceAction`, `SegmentProblemMark`, `ArchivedMark` и др.) и файлы, обойдённые в PR 4 [UI-81].
  Проверка — по пяти правилам в `ui-baseline.json` нет ни одного файла
- [ ] 5.2 Тест храповика: фикстура с плашкой, карточкой руками, `rounded-lg`, `shadow-sm`, `text-xs`
  краснит своё правило при пустом уровне [UI-81]. Проверка — `ui-ratchet.test.mjs` зелёный, фикстуры
  красные
- [ ] 5.3 `docs/UI.md`: «Цвет», «Радиусы», «Тени», «Текст» — без упоминаний плашек и карточек руками;
  перечень правил храповика в `CLAUDE.md` и `AGENTS.md` — пять правил «на нуле» — одним коммитом.
  Проверка — ревью PR; `AgentRulesCopyTests` зелёный

## 6. Верификация

- [ ] 6.1 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 6.2 `cd src/client && npm run ui:ratchet` — пять правил на нуле; `npm run lint:ratchet` — без роста
- [ ] 6.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~AgentRulesCopy"`
  — зелёный
- [ ] 6.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный
- [ ] 6.5 В описании каждого PR — снимки экранов раздела до и после в обеих темах на 1366×768 и 375 px
  (зонтик, 5.2), для мест «макет» — макет, вариант и ссылка; весь `npm run -s test:e2e:ci` зелёный (5.1)
- [ ] 6.6 PR с меткой `version:minor`
