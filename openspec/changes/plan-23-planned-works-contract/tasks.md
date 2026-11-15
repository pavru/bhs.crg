# Tasks: контракт «Плановые работы»

## 1. Интерфейс и поставщик

- [x] 1.1 `BHS.CRG.Modules/Exchange/PlannedWorks.cs`: `[ModuleContract("plan", "Плановые работы")]`,
  `IPlannedWorks.ReadAsync(ModuleReader, PlannedWorksQuery, ct)`, части ответа и `Unmapped` [CORE-34]
  [PLAN-9.1]. Проверка — `ModuleBoundaryTests` зелёный
- [x] 1.2 `Modules.Plan/Exchange/PlannedWorksProvider.cs` над методом чтения плана `plan-06` (только
  действующие редакции, только сопоставленные строки; число несопоставленных и строки-материалы — новыми
  методами `PlanRows`), распределение из `plan-10` (смета на разделе — целиком в раздел), отбор по разделу
  долей строки, нормы в единице классификатора, материалы строк-материалов с `planItemId: null`
  [PLAN-5.2] [PLAN-4] [PLAN-6.1] [PLAN-5.1] [PLAN-9.1]. Проверка — `PlannedWorksContractTests`:
  действующая редакция 2 из двух; несопоставленная строка — в `Unmapped`; «100 м» с коэффициентом 100 —
  норма на «м»; части строки — в распределении, отбор по разделу — доля; материал строки-материала — без
  позиции; `PlanRowsInventoryTests` зелёный
- [x] 1.3 Части по правам и `Hidden(permission)` [PLAN-23] [PLAN-11]. Проверка — тот же тест: только
  `hint` — без количеств; только `materials` — без объёмов и денег; без `plan.estimate.cost` — ни одного
  денежного поля; система — отказ; «скрыто» у `materials`, `labor`, `hint` называет узкое право
- [x] 1.4 Регистрация `AddModuleContract<IPlannedWorks, PlannedWorksProvider>("plan")` [CORE-34].
  Проверка — `ModuleContractCatalogTests` видит контракт; на хосте «только планирование» порт отвечает
  `Connected`; хост `costs` без `plan` — `NotConnected` с «Планирование» (`CostsOnlyHostTests`)

## 2. Права контракта

- [x] 2.1 `PlanModule.Permissions`: `plan.estimate.materials` (решение В-2), `plan.labor.read`,
  `plan.estimate.hint` с признаком контракта и `ReadAllMark.Out` [PLAN-23] [AUTH-8.1] [WORK-57]
  [WORK-59]. Проверка — тест состава `/api/account/access` по системным ролям
  (`PlanOnlyHostTests.System_roles_get_exactly_their_plan_permissions`): «Снабженец» и «Монтажник» модуля
  `plan` не получают, права у них в составе есть, адрес `/api/plan` им закрыт; «Менеджер проекта» —
  плюс `plan.labor.read`; «Руководитель» — без прав контракта. Состав «Монтажника» вне
  `plan.estimate.hint` не меняется
- [x] 2.2 `PlanOnlyHostTests`: число прав `plan.` и объяснение обновлены; права контракта названы среди
  прав без двери с причиной [PLAN-23]. Проверка — тест зелёный
- [x] 2.3 `docs/ADMIN_GUIDE.md`: права контракта `plan` — что дают, кому выданы, почему модуль не
  открывают; `cd docs/tools && npm run toc` [PLAN-23]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PlannedWorks|FullyQualifiedName~ModuleContract|FullyQualifiedName~AccountAccess|FullyQualifiedName~PlanOnlyHost|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~PlanRowsInventory|FullyQualifiedName~CostsOnlyHost"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
