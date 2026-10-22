# Proposal: «право контракта» — право, которое не открывает модуль-владелец

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D3, D2, D15).

> **Снят решением нарезки этапа 3 (22.10.2026, D18 зонтика).** «Право контракта» целиком строит
> `plan-21-contract-permissions`: первые права контракта — этапа 3. Change остаётся до архивации
> `plan-21` как запись того, что было запланировано здесь, и удаляется без архивации. Его дельта `auth`
> снята (`skip_specs: true`): те же требования несёт `plan-21`, и иначе они легли бы второй раз.

## Why

Модуль открывается тому, у кого есть любое его право (`AUTH-8.1`). Правило держит префикс кода
(`BHS.CRG.Modules/ModuleAccess.cs:13-17`). Этап 4 выдаёт права одного модуля ролям другого:
- `work.facts.read` — «Инженеру ИД» и «Производителю работ»;
- `costs.materials.read` и узкое `costs.materials.hint` — монтажнику и ИД (`WORK-48`).

Оба первых уже вписаны в составы ролей (`Api/Auth/SystemRoles.cs:59,117`). Объявленные обычными
правами, они открыли бы «Инженеру ИД» ворота `module:work` и пустой раздел «Учёт работ» в навигации.
Решение владельца Р-3: вид права «право контракта».

## What Changes

- `AppPermission` получает признак «право контракта». Объявить его может только модуль; у права ядра
  признак — отказ при старте.
- Право контракта не учитывается правилом «модуль открыт». Значит, оно не открывает:
  - ворота `module:<код>`;
  - `available` в `/api/account/access`, а с ним навигацию и пространства;
  - аудиторию уведомлений модуля;
  - журнал действий модуля;
  - опубликованные наборы модуля.
- Ворота `perm:<код>` на праве контракта работают как обычно. Адрес группы модуля-владельца им всё
  равно не открыть: группу закрывают ворота модуля.
- Редактор ролей и «Мои права» показывают признак: `contract: true` и подпись «право контракта: не
  открывает модуль „…“».
- Ни одного права контракта этот change **не объявляет**:
  - `costs.materials.read` и `costs.materials.hint` объявляет `work-09`;
  - `work.facts.read` объявляет `work-27`.
  Сторож проверяется на тестовом модуле.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- ~~`auth`~~ — дельта перенесена в `plan-21-contract-permissions`. Было: правило доступа к модулю (`AUTH-8.1`) с исключением для права контракта; признак в редакторе
  ролей и профиле.

## Impact

- **Сервер:**
  - `BHS.CRG.Modules/AppPermission.cs`, `ModuleAccess.cs`, `PermissionCatalog.cs`, `PermissionPolicies.cs`;
  - вызовы `ModuleAccess.IsOpen` в `Api/Endpoints/Account/AccountEndpoints.cs`,
    `Api/Notifications/PermissionAudience.cs`, `Api/Activity/ActivityActionCatalog.cs`,
    `Api/Auth/DataAccessResolver.cs`;
  - `Api/Auth/RoleEditor.cs` (`PermissionView`).
- **Клиент:** подпись признака в редакторе ролей (`features/settings/RolesPage.tsx`) и в `features/account/MyPermissionsSection.tsx`.
- **Тесты:** новый `ContractPermissionTests`; правка `McpGateInventoryTests`, `ReadAllWritesNothingTests`,
  `PermissionCatalogTests`.
- **Документы:** `docs/ADMIN_GUIDE.md` (что значит «право контракта» в редакторе ролей).
