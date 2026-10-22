# Tasks: варианты вида работ

## 1. Тип ядра

- [ ] 1.1 `CoreRecordTypes.WorkVariant` (`ВариантРаботы`): запертое обязательное `ВидРаботы` (`complex`,
  цель `ВидРаботы`), `Наименование` с `identity:1`, `Extendable`, `SkipWhenBlocked`; в `All` после
  `WorkType` [WORK-49] [TYPE-9] [CORE-30]. Проверка — тест проекции: старт заводит тип; на чистой базе
  без единиц старт проходит и типа нет; удаление поля `ВидРаботы` в редакторе — отказ с ключом

## 2. Дверь `/api/work-variants`

- [ ] 2.1 `Api/Endpoints/Core/WorkVariantEndpoints.cs`: `GET` под `core.catalog.read` с обязательным
  `purpose`; `POST`, `PUT`, `DELETE`, `archive`, `unarchive` под `core.worktypes.edit` (константа в
  `CorePermissions`) [WORK-49] [AUTH-9]. Проверка — `WorkVariantEndpointsTests`: 403 без права (в том
  числе с одним `core.catalog.edit`), `EndpointGateInventoryTests` зелёный, `ArchiveReadInventoryTests`
  с новым местом чтения
- [ ] 2.2 `If-Match` на `PUT`, `DELETE`, `archive`, `unarchive` [WORK-49] [AUTH-9]. Проверка — тесты:
  без заголовка — 400, устаревшая версия — 409, запись не меняется
- [ ] 2.3 Правило записи: ссылка, а не вложенный объект; вид существует и не в архиве; `workTypeId`
  постоянен; наименование уникально среди неархивных вариантов вида [WORK-49] [CORE-34.4]. Проверка —
  по тесту на каждый отказ, с именем поля в ответе
- [ ] 2.4 Удаление занятого — отказ с числом держателей; архив и возврат [CORE-34.1] [CORE-34.4].
  Проверка — тест с держателем-заглушкой в схеме модуля (как `OccupiedRecordDeleteTests`): отказ,
  после архива — нет в `choice`, есть в `display` с `archived`
- [ ] 2.5 `CommonDataEndpoints.cs`: create, update, delete, archive записи типа `ВариантРаботы` — 409 с
  адресом двери; `/purge` не трогается [WORK-49]. Проверка — тест на каждый из четырёх адресов

- [ ] 2.6 ~~Дверь `/api/work-types` и `core.worktypes.edit` у «Инженера ИД»~~ — строит
  `plan-03-work-types-door` (D18 зонтика). Здесь: дверь вариантов — на общем помощнике двери `plan-03`
  [WORK-59]. Проверка — `WorkTypeEndpointsTests` (`plan-03`) зелёные без правки

## 3. Порт модулю

- [ ] 3.1 `IModuleWorkTypes` и `ModuleWorkTypesPort` (заводит `plan-03`): `Variants` у вида,
  `GetVariantsAsync(ids)` с видом-владельцем; ~~порт, признаки по тэгам, «не заведён»~~ — `plan-03`
  [WORK-25] [CORE-20.4]. Проверка — `ModuleWorkTypesPortTests`: вид без вариантов — пустой список;
  архивный вариант — с признаком; вариант чужого вида назван; `ModuleBoundaryTests` зелёный

## 4. Клиент и документы

- [ ] 4.1 `shared/api/commonData.ts` и `document-sets/catalog/CatalogResource.tsx`: запись типа
  `ВариантРаботы` — адресами двери, действия — по `core.worktypes.edit` [WORK-49]. Проверка — vitest на
  выбор адреса по типу; вручную под «Сметчиком»: вариант заводится, архивируется, удаление занятого —
  отказ с числом
- [ ] 4.2 `docs/ADMIN_GUIDE.md`: варианты вида работ — кто ведёт, архив вместо удаления, почему ссылка
  вариант → вид, а не список у вида; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend»
  (содержание) зелёная

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkVariant|FullyQualifiedName~ModuleWorkTypes|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ArchiveRead|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
