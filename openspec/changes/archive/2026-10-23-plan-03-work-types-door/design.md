# Design: дверь видов работ и порты справочников ядра

Дочерний change зонтика `plan-00-epic`, общая архитектура — `../plan-00-epic/design.md` (D2 — что забрано у
`work-06`, D8 — сопоставление, D15 — исполнители графика). Здесь только решения этого change; дверь и
порт повторяют спроектированное в `openspec/changes/work-06-work-type-variants/design.md` без вариантов.

## Context

Сверено по коду master `6b81e51e`.

- `ВидРаботы` объявлен ядром (`Application/Documents/CoreRecordTypes.cs:117-150`): код (`identity:1`),
  наименование (`identity:2`), `ЕдиницаИзмерения` — `complex`, обязательное, цель `ЕдиницаИзмерения`
  (`:130`), три признака с тэгами `work.*`; уровень `Extendable`, `SkipWhenBlocked: true`.
- `ЕдиницаИзмерения` кодом не объявлен — тип заводит человек, ядро знает только код (`CoreRecordTypes.cs:42`).
  Кратности у единицы нет: коэффициент пересчёта живёт в сопоставлении (`plan-08`, D8 зонтика).
- `Сотрудник` — `CoreRecordTypes.cs:66-90`, производный от `Персоны`, `SkipWhenBlocked`; дверь
  `/api/employees` под `core.employees.read` / `.edit` (`Api/Endpoints/Core/EmployeeEndpoints.cs:39-42`).
- `core.worktypes.edit` объявлено (`Api/Auth/CorePermissions.cs:138`), адресов не открывает. Записи пишет
  `/api/common-data` под `core.catalog.edit` (`CommonDataEndpoints.cs:17`).
- Порты регистрируются в `Api/Modules/Ports/ModulePorts.cs` (`IModuleCatalog` — `:49`). `IModuleCatalog`
  знает роли справочника и отдаёт `DataJson` строкой.

## Goals / Non-Goals

**Goals:** право `core.worktypes.edit` открывает дверь видов работ; сметчик заводит вид работы; модуль
получает вид работы с единицей, единицы и сотрудников типизированно.

**Non-Goals:**
- варианты вида работ (`ВариантРаботы`) и их место в порту — `work-06`;
- связь сотрудника с учётной записью — `work-01`; пояс стройки и «сегодня на стройке» — `work-11`;
- окно сопоставления и создание вида из него — `plan-08` (зовёт эту дверь).

## Decisions

- **Состав PR — один.**
  - `Api/Endpoints/Core/WorkTypeEndpoints.cs` — группа `/api/work-types`: `GET` (с обязательным `purpose`,
    `RecordsFor`, поиск `q`) под `core.catalog.read`; `POST`, `PUT /{id}`, `DELETE /{id}`,
    `POST /{id}/archive|unarchive` под `core.worktypes.edit`. Право — константой в `CorePermissions`.
    Тонкая дверь над теми же командами общих данных, как `EmployeeEndpoints`.
  - `CommonDataEndpoints.cs`: create, update, delete, archive записи типа `ВидРаботы` — 409 с адресом
    двери. `/purge` (`core.catalog.purge`, `CORE-34.2`) остаётся общим: это путь администратора.
  - Порты, по одному файлу:
    - `IModuleWorkTypes.GetAsync(ids)`, `SearchAsync(q, limit)` → `ModuleWorkType(Id, Code, Name, UnitId,
      UnitName, RequiresLocation, RequiresMaterials, ProducesId, Archived)`; `null` — справочник не заведён.
      Единица — по цели поля (`Target: ЕдиницаИзмерения`), признаки — по тэгам: ключи заказчик вправе
      менять. Форма расширяема: `work-06` добавит `Variants`.
    - `IModuleUnits.GetAsync(ids)`, `SearchAsync(q)` → `ModuleUnit(Id, Name, Archived)`.
    - `IModuleEmployees.GetAsync(ids)`, `SearchAsync(q, onDate)` → `ModuleEmployee(Id, FullName,
      PersonnelNumber, EmployedFrom, EmployedTo, Archived)`; поля — по тэгам `employee.*`.
  - Реализации — `Api/Modules/Ports/ModuleWorkTypesPort.cs`, `ModuleUnitsPort.cs`, `ModuleEmployeesPort.cs`,
    регистрация рядом с `IModuleCatalog` в `ModulePorts.cs`.
  - Клиент: `shared/api/commonData.ts` — запись типа `ВидРаботы` идёт адресами двери;
    `CatalogResource.tsx` показывает действия записи по `core.worktypes.edit`.
- **Порты — чтение, без права модуля.** Справочники ядра читает любой вошедший (`core.catalog.read`),
  модуль получает их от имени ядра. Изоляции у справочников нет (`CatalogScope` — не граница, CLAUDE.md).
- **«Инженер ИД»** (`SystemRoles.cs`) правил виды работ через `core.catalog.edit`; чтобы правка не пропала
  молча, роль получает `core.worktypes.edit` — тест состава доступа по ролям в этом же PR.
- **If-Match** на всех пишущих адресах двери, включая архив: общий `/api/common-data` архивирует без
  версии, новая дверь старого послабления не наследует. Заведение версии не называет. Ворот модуля нет —
  адреса ядра.
- **Сторож.**
  - `WorkTypeEndpointsTests`: 403 без права; 400 без `If-Match`, 409 с устаревшей версией; отказ
    `/api/common-data` с адресом двери; «Сметчик» без `core.catalog.edit` заводит вид.
  - `ModuleWorkTypesPortTests`: единица и признаки при переименованных ключах; «не заведён» ≠ пусто.
  - `ModuleEmployeesPortTests`: ФИО и табельный по тэгам; «справочник „Сотрудник“ не заведён».
  - `EndpointGateInventoryTests` видит группу; `ArchiveReadInventoryTests` — новые места чтения с вердиктом.
- **Чем ломается.** Вид работы правится общим адресом под `core.catalog.edit` — сметчик не заводит вид из
  окна сопоставления. Единица читается по ключу `ЕдиницаИзмерения` — после переименования поля позиция
  перечня заводится без единицы.
- **Зависимости.** Нет. Потребители: `plan-08` (сопоставление, создание вида), `plan-19` (исполнители),
  `work-06` (варианты в порту), `work-11` и `work-13`.

## Risks / Trade-offs

- [Иной путь пишет виды работ мимо двери] → перепись мест записи записей типа `ВидРаботы`; найденное
  место либо идёт через дверь, либо названо исключением с причиной.
- [Справочник единиц не заведён — нет и видов работ] → `SkipWhenBlocked`, порт говорит «не заведён»,
  модуль `plan` показывает это сметчику, а не пустой выбор.
- [Этап 4 строит `work-06` по старому тексту] → пометка в `work-00-epic` и в `work-06` тем же PR нарезки.
