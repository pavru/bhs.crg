# Proposal: дверь видов работ и типизированные порты видов работ, единиц и сотрудников

Дочерний change зонтика `plan-00-epic`, общая архитектура — `../plan-00-epic/design.md` (D2, D8, D15).

## Why

Сметчик сопоставляет строки ЛСР с видами работ классификатора и заводит новый вид прямо из окна
сопоставления — классификатор ведёт тот же сметчик (`PLAN-6.1`, `CORE-8`). Сегодня он этого не может:
право `core.worktypes.edit` объявлено (`Api/Auth/CorePermissions.cs:138`) и выдано «Сметчику», но ни один
адрес его не проверяет — вид работы правится общим `/api/common-data` под `core.catalog.edit`
(`Api/Endpoints/Documents/CommonDataEndpoints.cs:17`), а его у «Сметчика» нет. Решение владельца
`WORK-59`: виды работ правятся своей дверью под `core.worktypes.edit`.

Модулю `plan` нужны вид работы с его единицей (ключ позиции перечня — единица классификатора, `PLAN-6.1`),
единицы измерения (выбор единицы и коэффициента в сопоставлении) и сотрудники (исполнители работы графика,
`PLAN-16`). `IModuleCatalog` знает только роли «организация» и «номенклатура» и отдаёт `DataJson` строкой
— единицу пришлось бы выковыривать из JSON по ключу, который заказчик вправе переименовать.

Дверь и порт спроектированы в `work-06` этапа 4; их первый потребитель — здесь (D2 зонтика). `work-06`
оставляет себе варианты вида работ.

## What Changes

- Дверь ядра `/api/work-types`: чтение под `core.catalog.read`; заведение, правка, удаление, архив и
  возврат из архива — под `core.worktypes.edit`; каждый пишущий адрес, кроме заведения, — с `If-Match`.
- `/api/common-data` записи типа `ВидРаботы` не заводит, не правит, не удаляет и не архивирует — 409 с
  адресом двери. Принудительное удаление (`/purge`) остаётся общим.
- «Инженер ИД» получает `core.worktypes.edit`, чтобы не потерять правку видов работ, которую имел через
  `core.catalog.edit`.
- Типизированные порты модулю (`BHS.CRG.Modules/Ports`):
  - `IModuleWorkTypes` — вид работы: код, наименование, единица (по цели поля, а не по ключу), признаки
    `work.*` по тэгам, архив; поиск по коду и наименованию; «справочник не заведён» ≠ «пусто»;
  - `IModuleUnits` — единица измерения: наименование, архив; поиск;
  - `IModuleEmployees` — сотрудник: ФИО, табельный номер, период работы, архив; поиск. Чтение — без связи
    с учётной записью (связь — `work-01`).
- Клиент: запись вида работы в каталоге «Данных» идёт адресами двери, действия записи показываются по
  `core.worktypes.edit`. Экран не меняется — макеты не нужны (`docs/UI.md`, «Макеты не нужны»).

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `core`: дверь видов работ под `core.worktypes.edit`; типизированные порты видов работ, единиц и
  сотрудников для модулей.

## Impact

- **Сервер:**
  - новый `Api/Endpoints/Core/WorkTypeEndpoints.cs` (по образцу `EmployeeEndpoints.cs`);
  - отказ записи типа в `Api/Endpoints/Documents/CommonDataEndpoints.cs`;
  - новые `BHS.CRG.Modules/Ports/IModuleWorkTypes.cs`, `IModuleUnits.cs`, `IModuleEmployees.cs` и
    реализации в `Api/Modules/Ports/`, регистрация в `ModulePorts.cs`;
  - `Api/Auth/SystemRoles.cs` («Инженер ИД»), константа права в `CorePermissions`.
- **Клиент:** `shared/api/commonData.ts`, `features/document-sets/catalog/CatalogResource.tsx`.
- **Тесты:** `WorkTypeEndpointsTests`, `ModuleWorkTypesPortTests`, `ModuleEmployeesPortTests`,
  `EndpointGateInventoryTests`, `ArchiveReadInventoryTests`, тест состава доступа по ролям.
- **Документы:** `docs/ADMIN_GUIDE.md` — кто ведёт классификатор видов работ.
- **Этап 4:** `work-06` сокращается до вариантов вида работ и их места в порту (пометка в `work-00-epic`).
