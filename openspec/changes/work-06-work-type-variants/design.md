# Design: варианты вида работ

Общая архитектура — `../work-00-epic/design.md` (D7 — вариант колонкой строки, D2). Здесь только
решения этого change.

## Context

Сверено по коду master `f8a8b222`.

- `ВидРаботы` объявлен ядром (`Application/Documents/CoreRecordTypes.cs:117-147`): код, наименование,
  единица, три признака с тэгами `work.*`; уровень `Extendable`, `SkipWhenBlocked: true` — на чистой
  установке без справочника единиц тип не заводится. Поля «Варианты» нет: массив целью в объявлении
  не бывает (`CoreRecordTypes.cs:52-53`, `ModuleFieldSpec.Target` — только `complex`).
- `core.worktypes.edit` объявлено (`Api/Auth/CorePermissions.cs:138`) и выдано «Сметчику»
  (`Api/Auth/SystemRoles.cs:75-78`), но ни один адрес его не проверяет. Записи пишет
  `/api/common-data` под `core.catalog.edit` (`Api/Endpoints/Documents/CommonDataEndpoints.cs:17`), а
  его у «Сметчика» нет: сегодня сметчик не правит классификатор вовсе.
- Образец двери ядра над общими данными — `Api/Endpoints/Core/EmployeeEndpoints.cs`: тонкая, зовёт
  те же `CommonData`-команды, своя — ради права.
- Порт модулю `IModuleCatalog` (`BHS.CRG.Modules/Ports/IModuleCatalog.cs:139`) знает роли справочника и
  отдаёт `DataJson` строкой — признаки вида работы модулю пришлось бы выковыривать из JSON по ключам.

## Goals / Non-Goals

**Goals:** справочник вариантов с постоянным идентификатором, запретом удалять занятое и архивом;
право `core.worktypes.edit` начинает что-то открывать; типизированный порт для `work-13`.

**Non-Goals:**
- дверь самого `ВидаРаботы` под `core.worktypes.edit` — вне change (сметчик не правит и виды работ;
  см. Risks);
- проверка «вариант чужого вида» в строке отчёта и объявление колонки-держателя — `work-13`;
- порт сотрудника `IModuleEmployees`, пояс `ModuleConstruction` и «сегодня на стройке» в `IModuleClock` —
  строит `work-11` своей дельтой `core`. Здесь только вид работы, его варианты (Р-4) и чтение признаков.

## Decisions

- **Состав PR — один.**
  - `CoreRecordTypes.WorkVariant` (`ВариантРаботы`): `ВидРаботы` — `complex` с целью `ВидРаботы`,
    `Required`, `Locked`; `Наименование` — `identity:1`, `Required`. `Parent: null`, `Extendable`,
    `SkipWhenBlocked: true`. В `All` — после `WorkType`: порядок значим, цель обязана существовать.
  - `Api/Endpoints/Core/WorkVariantEndpoints.cs` — группа `/api/work-variants`: `GET` (с `workTypeId`
    и обязательным `purpose`, `RecordsFor`) под `core.catalog.read`; `POST`, `PUT /{id}`,
    `DELETE /{id}`, `POST /{id}/archive|unarchive` под `core.worktypes.edit`. Право — константой в
    `CorePermissions` (сейчас литерал).
  - Правило записи варианта — в двери, до команды: ссылка, а не вложенный объект; вид существует и не в
    архиве (`CORE-34.4`, отказ с именем поля); `workTypeId` правкой не меняется; наименование уникально
    среди неархивных вариантов вида.
  - `CommonDataEndpoints.cs`: create, update, delete, archive записи типа `ВариантРаботы` — 409 с
    адресом двери. Принудительное удаление (`/purge`, `core.catalog.purge`, `CORE-34.2`) остаётся общим:
    это путь администратора для записей, занятых выключенным модулем, а не правка.
  - Порт `BHS.CRG.Modules/Ports/IModuleWorkTypes.cs`: `GetAsync(ids)` → `ModuleWorkType(Id, Code, Name,
    UnitId, RequiresLocation, RequiresMaterials, ProducesId, Archived, Variants)`, `ModuleWorkVariant(Id,
    WorkTypeId, Name, Archived)`; `null` — справочник не заведён. Признаки — по тэгам
    (`FunctionalTag.WorkRequiresLocation` и др.), не по ключам: ключи заказчик вправе менять
    (`CORE-7.3`). Реализация — `Api/Modules/Ports/ModuleWorkTypesPort.cs`, регистрация в `ModulePorts.cs:44`
    рядом с `IModuleJobs`.
  - Клиент: `shared/api/commonData.ts` — запись типа `ВариантРаботы` идёт адресами двери; действия
    записи показываются по `core.worktypes.edit` (`document-sets/catalog/CatalogResource.tsx`). Экран не
    меняется ни составом, ни раскладкой — макеты не нужны (`docs/UI.md`, «Макеты не нужны»).
- **Направление ссылки — вариант → вид**, а не массив вариантов у вида (`TYPE-9`). Так у варианта свой
  идентификатор и свой держатель, запрет удалять занятое работает поштучно, схема вида не меняется у
  заказчиков. Отличие от примера `TYPE-9` названо в `ADMIN_GUIDE.md`.
- **If-Match на всех пишущих адресах двери** (`AUTH-9`), включая архив: общий `/api/common-data`
  архивирует без версии (`CommonDataEndpoints.cs:185`), но новый адрес старого послабления не наследует.
  Заведение версии не называет — версии ещё нет. Ворот модуля нет: адреса ядра (`AUTH-8` не применимо).
- **Сторож.** `WorkVariantEndpointsTests` — 403 без права, 400/409 по `If-Match`, перенос на другой вид,
  архивный вид, дубль, отказ `/api/common-data`; `ModuleWorkTypesPortTests` — признак по тэгу при
  переименованном ключе, «не заведён» ≠ пусто; `EndpointGateInventoryTests` видит группу.
  `ArchiveReadInventoryTests` — новое место чтения с вердиктом `Choice` / `Display`.
- **Чем ломается.** Вариант строкой в JSON вида работ: дубль и переименование проходят, держателя нет.
  Дверь без запрета `/api/common-data` — правило «вид не меняется» обходится общей правкой.
- **Зависимости.** Нет от других change. Потребитель порта — `work-13` (строка с вариантом чужого вида
  не сохраняется, колонка варианта объявлена держателем `CORE-34.1`).

## Risks / Trade-offs

- [Сметчик не правит сам `ВидРаботы`: общий адрес требует `core.catalog.edit`] → вне change, выносится
  владельцу отдельной задачей (дверь видов работ по тому же образцу). Варианты сметчик ведёт с этого PR.
- [Справочник единиц не заведён — нет ни видов, ни вариантов] → тот же `SkipWhenBlocked`, порт
  говорит «не заведён», модуль `work` различает это с пустотой (`work-10`).
