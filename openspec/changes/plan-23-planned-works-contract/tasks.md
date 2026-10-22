# Tasks: контракт «Плановые работы»

## 1. Интерфейс и поставщик

- [ ] 1.1 `BHS.CRG.Modules/Exchange/PlannedWorks.cs`: `[ModuleContract("plan", "Плановые работы")]`,
  `IPlannedWorks.ReadAsync(ModuleReader, PlannedWorksQuery, ct)`, части ответа и `Unmapped` [CORE-34]
  [PLAN-9.1]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 `Modules.Plan/Exchange/PlannedWorksProvider.cs` над методом чтения плана `plan-06` (только
  действующие редакции, только сопоставленные строки), распределение из `plan-10`, нормы в единице
  классификатора [PLAN-5.2] [PLAN-4] [PLAN-6.1] [PLAN-5.1]. Проверка — `PlannedWorksContractTests`:
  действующая редакция 2 из двух; несопоставленная строка — в `Unmapped`; «100 м» с коэффициентом 100 —
  норма на «м»
- [ ] 1.3 Части по правам и `Hidden(permission)` [PLAN-23] [PLAN-11]. Проверка — тот же тест: только
  `hint` — без количеств; только `materials` — без объёмов и денег; без `plan.estimate.cost` — ни одного
  денежного поля; система — отказ
- [ ] 1.4 Регистрация `AddModuleContract<IPlannedWorks, PlannedWorksProvider>("plan")` [CORE-34].
  Проверка — `ModuleContractCatalogTests` видит контракт; хост `costs` без `plan` — `NotConnected`

## 2. Права контракта

- [ ] 2.1 `PlanModule.Permissions`: `plan.estimate.materials` (ждёт решения В-2), `plan.labor.read`,
  `plan.estimate.hint` с признаком контракта и `ReadAllMark.Out` [PLAN-23] [AUTH-8.1] [WORK-57]
  [WORK-59]. Проверка — тест состава `/api/account/access` по системным ролям: «Снабженец» и
  «Монтажник» модуля `plan` не получают, права у них в составе есть
- [ ] 2.2 `PlanOnlyHostTests`: число прав `plan.` и объяснение обновлены [PLAN-23]. Проверка — тест зелёный
- [ ] 2.3 `docs/ADMIN_GUIDE.md`: права контракта `plan` — что дают, кому выданы, почему модуль не
  открывают; `cd docs/tools && npm run toc` [PLAN-23]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PlannedWorks|FullyQualifiedName~ModuleContract|FullyQualifiedName~AccountAccess|FullyQualifiedName~PlanOnlyHost|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
