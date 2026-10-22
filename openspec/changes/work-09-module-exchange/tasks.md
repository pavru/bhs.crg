# Tasks: межмодульный контракт и «Материалы на объекте»

## 0. Условие

- [ ] 0.1 Проверить master: есть ли `BHS.CRG.Modules/Exchange` от этапа 3 [CORE-34]. Проверка — если
  есть, группа 1 снимается с пометкой в `design.md`, core-дельта сокращается до разницы и
  `openspec validate work-09-module-exchange --strict` зелёный

## 1. PR 1: механизм контракта

- [ ] 1.1 `BHS.CRG.Modules/Exchange/ModuleContractAttribute.cs`, `IModuleContracts.cs`
  (`Connected` | `NotConnected`), общие «скрыто правами» и отказ системы; регистрация
  `AddModuleContract<T, Impl>(module)` [CORE-34] [OVW-12]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 `Api/Modules/ModuleContractCatalog.cs` — проверка при старте: одна реализация у включённого
  поставщика, регистрирует названный модуль [CORE-34]. Проверка — `ModuleContractCatalogTests` на
  пробных модулях: без реализации, две реализации, чужая регистрация — отказ старта с названиями
- [ ] 1.3 Порт потребителю: выключенный поставщик — `NotConnected` с названием модуля; прямой запрос
  интерфейса контракта из DI ловит перепись [CORE-34]. Проверка — тест: хост без поставщика отвечает
  «не подключено», образец прямого запроса — перепись красная
- [ ] 1.4 Контракт принимает `ModuleReader` (`work-07`); система — отказ с причиной [CORE-34]
  [WORK-40]. Проверка — тест пробного контракта: без права — «скрыто правами», система — отказ
- [ ] 1.5 `docs/DEV_NOTES.md`: как завести контракт, почему поставщик назван у интерфейса. Проверка —
  ревью PR

## 2. PR 2: поставщик `costs` и права контракта

- [ ] 2.1 `BHS.CRG.Modules/Exchange/MaterialsOnSite.cs` и `Modules.Costs/Exchange/MaterialsOnSiteContract.cs`
  над `IssuedMaterials.ReadAsync`: полный ответ, подсказка, «скрыто» [COST-17] [COST-18] [WORK-5.1].
  Проверка — `MaterialsOnSiteContractTests`: две накладные — количество и даты; только `hint` — без
  количеств; черновик не входит; ни одного права — «скрыто»
- [ ] 2.2 `CostsModule.cs`: `costs.materials.read` и `costs.materials.hint` — права контракта
  (`work-02`), `ReadAllMark.Out` с причиной; комментарий `CostsModule.cs:79-83` переписан [COST-28]
  [WORK-48] [AUTH-8.1]. Проверка — тест состава `/api/account/access` по системным ролям: у
  «Инженера ИД» и «Монтажника» нет модуля `costs`, `/api/costs/*` им — 403
- [ ] 2.3 `SystemRoles.cs:64` — «Монтажнику» `costs.materials.hint` [WORK-48]. Проверка — тот же тест
  состава: право есть, пространства «Счета и накладные» нет
- [ ] 2.4 Живой хост `work` без `costs`: контракт — «не подключено»; `CostsOnlyHostTests` зелёный
  [CORE-34]. Проверка — `WorkOnlyHostTests` (из `work-10`) дополнен этим случаем
- [ ] 2.5 `docs/ADMIN_GUIDE.md`: права контракта — что дают, почему не открывают модуль; у кого по
  умолчанию; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend» (содержание) зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModuleContract|FullyQualifiedName~MaterialsOnSite|FullyQualifiedName~AccountAccess|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~CostsOnlyHost|FullyQualifiedName~WorkOnlyHost"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
