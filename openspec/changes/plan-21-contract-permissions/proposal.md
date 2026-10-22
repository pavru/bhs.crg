# Proposal: «право контракта» — право, которое не открывает модуль-владелец

Дочерний change зонтика `plan-00-epic`, общая архитектура — `../plan-00-epic/design.md` (D2, D11, D12).
Забирает целиком `work-02-contract-permissions` этапа 4.

## Why

Модуль открывается тому, у кого есть любое его право (`AUTH-8.1`). Правило держит префикс кода
(`BHS.CRG.Modules/ModuleAccess.cs:13-17`). Этап 3 первым выдаёт права одного модуля ролям, которым
сам модуль не нужен:
- `plan.estimate.materials` — «Снабженцу» для заявок `costs` (решение В-2);
- `plan.labor.read` — «Менеджеру проекта» для «по смете — фактически» (`WORK-57`);
- `plan.estimate.hint` — «Монтажнику» для яруса «смета» подсказки материалов (`WORK-59`);
- у `costs` — право полосы затрат в «Ходе работ» (решение В-3).

Объявленные обычными правами, они открыли бы «Монтажнику» ворота `module:plan` и пустой раздел
«Планирование» в навигации. По правилу «поддержка едет с первым потребителем» механизм, записанный в
этапе 4 задачей `work-02`, строится здесь.

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
- Ни одного права контракта этот change **не объявляет**: права `plan` объявляет `plan-23`, право полосы
  затрат `costs` — `plan-24`, права этапа 4 — `work-09` и `work-27`. Сторож проверяется на тестовом модуле.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `auth`: правило доступа к модулю (`AUTH-8.1`) с исключением для права контракта; признак в редакторе
  ролей и профиле.

## Impact

- **Сервер:**
  - `BHS.CRG.Modules/AppPermission.cs`, `ModuleAccess.cs`, `PermissionCatalog.cs`, `PermissionPolicies.cs`;
  - вызовы `ModuleAccess.IsOpen` в `Api/Endpoints/Account/AccountEndpoints.cs`,
    `Api/Notifications/PermissionAudience.cs`, `Api/Activity/ActivityActionCatalog.cs`,
    `Api/Auth/DataAccessResolver.cs`;
  - `Api/Auth/RoleEditor.cs` (`PermissionView`).
- **Клиент:** подпись признака в редакторе ролей (`features/settings/RolesPage.tsx`) и в
  `features/account/MyPermissionsSection.tsx`.
- **Тесты:** новый `ContractPermissionTests`; правка `McpGateInventoryTests`, `ReadAllWritesNothingTests`,
  `PermissionCatalogTests`.
- **Документы:** `docs/ADMIN_GUIDE.md` (что значит «право контракта» в редакторе ролей).
- **Этап 4:** `work-02` снимается — его делает этот change (пометку в `work-00-epic` ставит нарезка).
