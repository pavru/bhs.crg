# Tasks: дверь видов работ и порты справочников ядра

## 1. Дверь видов работ

- [ ] 1.1 Константа `core.worktypes.edit` в `CorePermissions`; `Api/Endpoints/Core/WorkTypeEndpoints.cs` —
  `/api/work-types`: `GET` (`purpose`, `q`) под `core.catalog.read`; `POST`, `PUT /{id}`, `DELETE /{id}`,
  `POST /{id}/archive|unarchive` под `core.worktypes.edit`, `If-Match` на всех пишущих, кроме заведения
  [WORK-59] [CORE-8] [AUTH-9]. Проверка — `WorkTypeEndpointsTests`: 403 без права, 400 без `If-Match`, 409 с
  устаревшей версией, «Сметчик» без `core.catalog.edit` заводит вид
- [ ] 1.2 `CommonDataEndpoints.cs`: запись типа `ВидРаботы` — 409 с адресом двери; `/purge` остаётся
  [WORK-59]. Проверка — тест: create, update, delete, archive общим адресом отвергнуты с адресом
- [ ] 1.3 `SystemRoles.cs`: «Инженер ИД» получает `core.worktypes.edit` [WORK-59]. Проверка — тест состава
  `/api/account/access` по системным ролям
- [ ] 1.4 `EndpointGateInventoryTests`, `ArchiveReadInventoryTests` — новая группа и места чтения с
  вердиктом [AUTH-9] [CORE-34.4]. Проверка — переписи зелёные

## 2. Порты модулю

- [ ] 2.1 `BHS.CRG.Modules/Ports/IModuleWorkTypes.cs` и `Api/Modules/Ports/ModuleWorkTypesPort.cs`: единица
  по цели поля, признаки по тэгам, поиск, «не заведён» ≠ пусто; регистрация в `ModulePorts.cs`
  [CORE-20.4] [PLAN-6.1]. Проверка — `ModuleWorkTypesPortTests`: переименованные ключи единицы и признака;
  экземпляр без справочника единиц
- [ ] 2.2 `IModuleUnits.cs` и `ModuleUnitsPort.cs` [PLAN-6.1]. Проверка — тест порта: поиск, архивная
  единица помечена
- [ ] 2.3 `IModuleEmployees.cs` и `ModuleEmployeesPort.cs`: ФИО и табельный по тэгам `employee.*`, период
  работы, поиск на дату [PLAN-16]. Проверка — `ModuleEmployeesPortTests`: уволенный помечен, «справочник
  „Сотрудник“ не заведён»
- [ ] 2.4 `ModuleBoundaryTests` зелёный — порты лежат в контрактах, реализация в `Api` [CORE-2]. Проверка —
  тест зелёный

## 3. Клиент и документы

- [ ] 3.1 `shared/api/commonData.ts`: запись типа `ВидРаботы` идёт адресами `/api/work-types` с
  `If-Match`; `CatalogResource.tsx` — действия записи по `core.worktypes.edit` [WORK-59]. Проверка —
  vitest на выбор адреса записи по типу
- [ ] 3.2 `docs/ADMIN_GUIDE.md`: кто ведёт классификатор видов работ, почему не через общий адрес;
  `cd docs/tools && npm run toc` [CORE-8]. Проверка — проверка содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkTypeEndpoints|FullyQualifiedName~ModuleWorkTypesPort|FullyQualifiedName~ModuleEmployeesPort|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ArchiveReadInventory|FullyQualifiedName~AccountAccess"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
