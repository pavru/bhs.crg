# Tasks: каркас модуля `work`

## 1. Проект и объявление модуля

- [ ] 1.1 Создать `src/server/BHS.CRG.Modules.Work` (csproj со ссылками только на `BHS.CRG.Modules` и
  `BHS.CRG.Domain`), вписать в `BHS.CRG.slnx` и `BHS.CRG.Api.csproj` [CORE-1]. Проверка —
  `cd src/server && dotnet build BHS.CRG.slnx` без ошибок, `ModuleBoundaryTests` зелёный
- [ ] 1.2 `WorkModule.cs`: код `work`, название «Учёт работ», `RoutePrefixes = ["/api/work"]`, пустая
  группа за воротами, `UsesSections` — стройки, справочники, сотрудники; вписать в `DeliveredModules.cs`,
  `CoreSectionsTests` и `deploy/.env.example` [CORE-1] [AUTH-17]. Проверка — `EndpointGateInventoryTests`,
  `ModulePrefixCoverageTests` и `CoreSectionsTests` зелёные
- [ ] 1.3 Объявить права `work.report.own`, `work.report.review`, `work.assign`, `work.sites.all`,
  `work.report.read.all` с объяснениями и `ReadAllMark` [WORK-45] [WORK-46]. Проверка — новый тест
  состава `/api/account/access` по системным ролям: «Инженер ИД» не получает модуль `work`

## 2. Схема модуля

- [ ] 2.1 `WorkDbContext : ModuleDbContext` (схема `work`, история миграций в ней; конфликт записи — общий
  409 `ApiErrorMapping`) и `WorkDbContextFactory` без строки подключения [CORE-4]. Проверка —
  `ModuleSchemaDeclarationTests` и `ModuleDbContextInventoryTests` зелёные
- [ ] 2.2 Миграция `Initial`:
  `dotnet ef migrations add Initial --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`.
  Проверка — на пересозданной базе старт применяет миграции ядра, затем `work`
- [ ] 2.3 Команду миграции модуля вписать в `CLAUDE.md` и `AGENTS.md` одним коммитом. Проверка —
  `AgentRulesCopyTests` зелёный
- [ ] 2.4 Вписать контекст в переписи, которых не было в нарезке: `ModuleDbContextInventoryTests`,
  `BackupManifestCoverageTests.ContextCoverage`, `FixtureResetCoverageTests.ContextCoverage`,
  `ModuleFileInventoryTests`, `ModuleReferenceInventoryTests`, `OrganizationShortNameCensusTests` [CORE-4].
  Проверка — классы зелёные

## 3. Живой хост и справочники

- [ ] 3.1 `Integration/WorkOnlyHostTests.cs`: хост с `Modules__Enabled=work` на своей базе поднимается, а
  адреса `id`, `costs` и `plan` отвечают 501 [CORE-1]; хост «только `plan`» не видит `work`. Проверка —
  тест зелёный; ссылка на `Infrastructure` из модуля роняет `ModuleBoundaryTests`
- [ ] 3.2 «Справочник не заведён» для «Сотрудника» и «ВидаРаботы» (`SkipWhenBlocked`): на хосте «только
  `work`» без «Персоны» и единиц порты ядра (`IModuleEmployees`, `IModuleWorkTypes`) отвечают «не заведён»
  (`null`), а не пустым выбором, и их `NotSetUp` называет справочник. Перевод в отказ адреса модуля — с
  первым потребителем (`work-11`). Проверка — тест на чистой базе без «Персоны»

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkOnlyHost|FullyQualifiedName~PlanOnlyHost|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleSchema"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 Весь набор backend: `bash scripts/backend-tests.sh` — зелёный
