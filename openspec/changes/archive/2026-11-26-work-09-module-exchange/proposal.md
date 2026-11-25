# Proposal: межмодульный контракт и «Материалы на объекте» у `costs`

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D13, D3, D4).

> **Сужен решением нарезки этапа 3 (22.10.2026, D18 зонтика).** Механизм межмодульного контракта
> реализован в `plan-22-module-exchange`. Условие сработало, PR механизма и его core-дельта сняты.
> Здесь один PR поставщика `costs` «Материалы на объекте» с правами контракта из `plan-21`.

## Why

Модули читают данные друг друга только через контракты ядра, и при выключенном поставщике потребитель
говорит «не подключено», а не показывает пустоту (`CORE-34`, `OVW-12`). Первый контракт этапа —
«Материалы на объекте»: монтажнику в поле «применённые материалы» предлагается отпущенное на его
стройку (`WORK-5.1`, `COST-18`), узким правом `costs.materials.hint` — номенклатура без количеств и без
набора данных (`WORK-48`, Р-3). Сегодня перечень — внутренняя модель `costs`
(`Modules.Costs/Data/IssuedMaterials.cs`), наружу она не выходит, а право `costs.materials.read`
сознательно не объявлено «до первого потребителя» (`Modules.Costs/CostsModule.cs`), хотя уже
стоит в составах «Инженера ИД» и «Менеджера проекта» (`Api/Auth/SystemRoles.cs`). Без контракта
потребитель позвал бы `GET /api/costs/materials` по HTTP — мимо прав и мимо «не подключено».

## What Changes

- **Поставщик `costs`.** Контракт «Материалы на объекте» над `IssuedMaterials`: по
  `costs.materials.read` — позиции с количествами, датами и числом не вошедших строк; по
  `costs.materials.hint` — только позиции номенклатуры. Оба права объявляются **правами контракта**
  (`plan-21`): модуль `costs` и его навигацию они не открывают. «Монтажник» получает
  `costs.materials.hint`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `costs`: поставщик «Материалы на объекте»; права `costs.materials.read` и `costs.materials.hint` как
  права контракта.

## Impact

- **Сервер:** новый `BHS.CRG.Modules/Exchange/MaterialsOnSite.cs` (механизм `Exchange/*` и
  `ModuleContractCatalog.cs` — из `plan-22`); `Modules.Costs/Exchange/MaterialsOnSiteContract.cs`,
  `CostsModule.cs` (права, регистрация);
  `Api/Auth/SystemRoles.cs` («Монтажник»).
- **Тесты:** `MaterialsOnSiteContractTests`, тест состава
  `/api/account/access` по ролям, `ModuleBoundaryTests`, `CostsOnlyHostTests`, живой хост `work` без `costs`.
- **Документы:** `docs/ADMIN_GUIDE.md` — права контракта у ролей.
- Адрес `GET /api/costs/materials` не меняется.
