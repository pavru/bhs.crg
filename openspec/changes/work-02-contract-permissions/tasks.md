# Tasks: «право контракта»

## 1. Признак и правило доступа

- [ ] 1.1 `AppPermission.Contract` и множество прав контракта в `PermissionCatalog`. Признак у права
  ядра — отказ сборки справочника с названием права [AUTH-1] [AUTH-8.1]. Проверка — `PermissionCatalogTests`:
  право `core.*` с признаком роняет сборку
- [ ] 1.2 `ModuleAccess.IsOpen(code, granted, catalog)` без старой сигнатуры. Перевести все вызовы:
  `ModuleAccessHandler`, `AccountEndpoints`, `PermissionAudience`, `ActivityActionCatalog`,
  `DataAccessResolver`, тесты `McpGateInventoryTests`, `ReadAllWritesNothingTests` [AUTH-8] [AUTH-8.1]
  [AUTH-13]. Проверка — `cd src/server && dotnet build BHS.CRG.slnx` без ошибок, `PermissionCatalogTests` зелёный
- [ ] 1.3 `ContractPermissionTests` — живой хост с тестовым модулем: только право контракта —
  `available: false`, 403 на адресах группы, уведомление модуля не видно; вместе с обычным правом модуль
  открыт [AUTH-8.1] [WORK-48]. Проверка — тест зелёный и краснеет, если `IsOpen` снова считает по префиксу

## 2. Носитель для администратора

- [ ] 2.1 Поле `contract` в `PermissionView` (`/api/roles/permissions`, `/api/account/permissions`)
  [AUTH-5] [AUTH-16.7]. Проверка — `AccountAccessTests`: `PermissionDto` разбирает `contract`, у
  обычного права — `false`
- [ ] 2.2 Подпись «право контракта: не открывает модуль „…“» в `features/settings/RolesPage.tsx` и
  `features/account/MyPermissionsSection.tsx` [AUTH-5] [AUTH-16.7]. Проверка — vitest на подпись по
  признаку; `npm run ui:ratchet` без роста
- [ ] 2.3 `docs/ADMIN_GUIDE.md`: абзац «Право контракта» в главе о ролях. Перестроить содержание:
  `cd docs/tools && npm run toc` [AUTH-8.1]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ContractPermission|FullyQualifiedName~PermissionCatalog|FullyQualifiedName~AccountAccess|FullyQualifiedName~ReadAllWritesNothing|FullyQualifiedName~McpGateInventory|FullyQualifiedName~NotificationAudience"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
