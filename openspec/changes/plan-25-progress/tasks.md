# Tasks: «Ход работ» стройки и факт в графике

## 1. PR 1: ядро и контракты

- [ ] 1.1 `BHS.CRG.Modules/Exchange/ActualWorks.cs`: `[ModuleContract("work", "Фактические работы")]`,
  форма без часов и людей, `pending`, `crewUnreconciled` [CORE-34] [WORK-57] [STG-6.1]. Проверка —
  `ModuleBoundaryTests` зелёный; тест формы: в типе ответа нет полей часов и сотрудников
- [ ] 1.2 `BHS.CRG.Modules/Exchange/ProgressContribution.cs`: контракт `Many`, колонки, полосы,
  состояния [CORE-34] [PLAN-21]. Проверка — `ModuleContractCatalogTests` видит его как `Many`
- [ ] 1.3 `Api/Endpoints/Core/ConstructionProgressEndpoints.cs` и сборщик: строки по виду работы и
  единице, факт и затраты из контрактов, «скрыто правами», «не подключено»; ворота «любое из» прав,
  собранных из вкладов включённых модулей, без литерала в ядре (`plan-02`) [PLAN-21] [PLAN-21.1]
  [AUTH-8.2]. Проверка — `ConstructionProgressTests` с пробным модулем `work`: факт без плана и план без
  факта — строки есть; `work` выключен — полоса «не подключено», хост стартует, `work.facts.read` в
  списке ворот нет; без прав колонок — 403; ни одного вклада — 403; перепись ворот зелёная
- [ ] 1.4 `CoreSections.Progress` в `All`; `progress` в `UsesSections` модулей `plan` и `costs` [AUTH-16.1].
  Проверка — `CoreSectionsTests`: точные составы `plan` (`constructions`, `catalog`, `progress`) и `costs`
  (прежние и `progress`)
- [ ] 1.5 Пробный модуль `work` с засеянным фактом в `Tests/Common` и живой хост `plan` + пробный `work`
  [STG-6.1]. Проверка — тест 1.3 идёт на нём

## 2. PR 2: вклад `plan` и факт графика

- [ ] 2.1 `Modules.Plan/Exchange/PlanProgressContribution.cs`: колонки «план по смете» (через
  `IPlannedWorks`) и «срок по графику», полосы «план по: … ред. N», «не сопоставлено», «не
  распределено» [PLAN-21] [PLAN-5.2] [PLAN-6.1] [PLAN-5.1]. Проверка — `PlanProgressContributionTests`:
  действующая редакция в полосе; 17 несопоставленных; отбор по разделу — нераспределённое полосой;
  без `plan.estimate.read` — «скрыто правами»
- [ ] 2.2 Факт у работы в ответе графика (`plan-19`) из `IActualWorks` с состоянием [PLAN-18]. Проверка —
  `ScheduleFactTests`: `work` выключен — `not-connected`; пробный `work` — объём позиции и раздела

## 3. PR 3: экран (макеты П5)

- [ ] 3.1 Раунд П5: три варианта «Хода работ» в Claude Design (таблица, полосы, отбор по разделу,
  состояния колонок), выбор владельца; ссылку вписать сюда [PLAN-21]. Проверка — ссылка на выбранный
  вариант в `design.md`
- [ ] 3.2 `src/client/src/shared/api/progress.ts` и `features/planning/progress/ProgressPanel.tsx` на
  `DataGrid`; регистрация панели «Ход работ» механизмом `plan-02`: «любое из» пяти прав, `section:
  'progress'`, блок «Работы» первой записью, уровни стройки и раздела [PLAN-21] [PLAN-20]. Проверка — vitest: колонка `hidden` — подпись с правом, `not-connected` — полоса вместо
  колонки
- [ ] 3.3 Полосы над таблицей общим компонентом состояния; затраты `costs` — «разнесено · из них
  оплачено», доля по работам — «не ведётся» [PLAN-21.1]. Проверка — vitest на три состояния
- [ ] 3.4 Колонка факта в списке графика (`plan-20`) с состоянием [PLAN-18]. Проверка — vitest:
  `not-connected` — строка «учёт работ не подключён», а не нули
- [ ] 3.5 Живой прогон `src/client/e2e/progress-smoke.mjs` (стенд без `work`): «Сметчик» и «Менеджер
  проекта» видят «Ход работ», полосу «учёт работ не подключён»; вписать в `test:e2e:ci` и
  `e2e/README.md` [PLAN-21]. Проверка — прогон зелёный на стенде
- [ ] 3.6 `docs/USER_GUIDE.md`: «Ход работ», полосы и оговорки; `cd docs/tools && npm run toc` [PLAN-21].
  Проверка — проверка содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ConstructionProgress|FullyQualifiedName~PlanProgress|FullyQualifiedName~ScheduleFact|FullyQualifiedName~ModuleContract|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
