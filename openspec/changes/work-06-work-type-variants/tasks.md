# Tasks: варианты вида работ

## 1. Тип ядра

- [x] 1.1 `CoreRecordTypes.WorkVariant` (`ВариантРаботы`): обязательное поле ядра `ВидРаботы`
  (`complex`, цель `ВидРаботы`, `Locked: false` — см. design), `Наименование` с `identity:1`,
  `Extendable`, `SkipWhenBlocked`; в `All` после `WorkType` [WORK-49] [TYPE-9] [CORE-30]. Проверка —
  тест проекции: старт заводит тип; на чистой базе без единиц старт проходит и типа нет; удаление поля
  `ВидРаботы` в редакторе — отказ с ключом

## 2. Дверь `/api/work-variants`

- [x] 2.1 `Api/Endpoints/Core/WorkVariantEndpoints.cs`: `GET` и `GET /{id}` под `core.catalog.read`,
  список — с обязательным `purpose`; `POST`, `PUT`, `DELETE`, `archive`, `unarchive` под
  `core.worktypes.edit` (`CorePermissions.WorkTypesEdit`); без справочника — 409 `directory-missing` с
  причиной, как у видов работ [WORK-49] [AUTH-9] [CORE-20.4]. Проверка — `WorkVariantEndpointsTests`:
  403 без права (в том числе с одним `core.catalog.edit`), «не заведён» на чистой базе,
  `EndpointGateInventoryTests` зелёный, `ArchiveReadInventoryTests` с новым местом чтения
- [x] 2.2 `If-Match` на `PUT`, `DELETE`, `archive`, `unarchive` [WORK-49] [AUTH-9]. Проверка — тесты:
  без заголовка — 400, устаревшая версия — 409, запись не меняется
- [x] 2.3 Правило записи: ссылка, а не вложенный объект; вид существует и не в архиве; `workTypeId`
  постоянен; наименование обязательно и уникально среди неархивных вариантов вида; «есть в архиве» — в
  пределах вида [WORK-49] [CORE-34.4]. Проверка — по тесту на каждый отказ, с именем поля в ответе;
  одноимённые варианты разных видов заводятся; архивный вариант своего вида — `archived-twin`
- [x] 2.4 Удаление занятого — отказ с числом держателей; архив и возврат [CORE-34.1] [CORE-34.4].
  Проверка — тест с держателем-заглушкой в схеме модуля (как `OccupiedRecordDeleteTests`): отказ,
  после архива — нет в `choice`, есть в `display` с `archived`
- [x] 2.5 Строка `ВариантРаботы` в `OwnDoorTypes.All`: create, update, delete, archive записи типа
  адресом `/api/common-data` — 409 с адресом двери; возврат из архива и `/purge` не трогаются [WORK-49].
  Проверка — тест на каждый из четырёх адресов, `OwnDoorWriteInventoryTests` зелёный

- [x] 2.6 ~~Дверь `/api/work-types` и `core.worktypes.edit` у «Инженера ИД»~~ — строит
  `plan-03-work-types-door` (D18 зонтика). Здесь: сверка версии и архив с версией в ответе выносятся из
  `WorkTypeEndpoints` в `OwnDoorWrites`, дверь вариантов — на нём же [WORK-59]. Проверка —
  `WorkTypeEndpointsTests` (`plan-03`) зелёные без правки

## 3. Порт модулю

- [x] 3.1 `IModuleWorkTypes` и `ModuleWorkTypesPort` (заводит `plan-03`): `Variants` у вида,
  `GetVariantsAsync(ids)` с видом-владельцем; ~~порт, признаки по тэгам, «не заведён»~~ — `plan-03`
  [WORK-25] [CORE-20.4]. Проверка — `ModuleWorkTypesPortTests`: вид без вариантов — пустой список;
  архивный вариант — с признаком; вариант чужого вида назван; `ModuleBoundaryTests` зелёный

## 4. Клиент и документы

- [ ] 4.1 `shared/api/commonData.ts`: строка `ВариантРаботы` в `OWN_DOORS` — запись адресами двери,
  действия каталога (`RecordWriteGate`) — по `core.worktypes.edit` [WORK-49]. Проверка — vitest на
  выбор адреса и права по типу (`commonData.door.test.ts`); вручную под «Сметчиком»: вариант
  заводится, архивируется, удаление занятого — отказ с числом
- [x] 4.2 `docs/ADMIN_GUIDE.md`: варианты вида работ — кто ведёт, архив вместо удаления, почему ссылка
  вариант → вид, а не список у вида; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend»
  (содержание) зелёная

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkVariant|FullyQualifiedName~WorkTypeEndpoints|FullyQualifiedName~ModuleWorkTypes|FullyQualifiedName~CoreRecordType|FullyQualifiedName~OwnDoorWrite|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ArchiveRead|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
