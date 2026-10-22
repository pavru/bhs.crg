# Tasks: каркас модуля `plan`

## 1. Проект и объявление модуля

- [ ] 1.1 Создать `src/server/BHS.CRG.Modules.Plan` (csproj со ссылками только на `BHS.CRG.Modules` и
  `BHS.CRG.Domain`), вписать в `BHS.CRG.slnx` и `BHS.CRG.Api.csproj` [CORE-1]. Проверка —
  `cd src/server && dotnet build BHS.CRG.slnx` без ошибок, `ModuleBoundaryTests` (с `plan` в `InlineData`)
  зелёный
- [ ] 1.2 `PlanModule.cs`: код `plan`, название «Планирование», `RoutePrefixes = ["/api/plan"]`, пустая
  группа за воротами; вписать в `DeliveredModules.cs` и `deploy/.env.example` [CORE-1] [AUTH-17].
  Проверка — `EndpointGateInventoryTests` и `ModulePrefixCoverageTests` зелёные
- [ ] 1.3 Объявить `plan.estimate.read`, `plan.estimate.edit` с объяснениями и `ReadAllMark` [PLAN-23].
  Проверка — `PlanOnlyHostTests`: число прав `plan.` названо в тесте с объяснением

## 2. Составы ролей

- [ ] 2.1 `SystemRoles.cs` по `PLAN-23`: у «Сметчика» убрать `plan.offer.edit`, добавить
  `plan.estimate.read`, `plan.estimate.cost`; «Менеджеру проекта» — права `plan` по `PLAN-23` и решению
  В-1 (`PLAN-Q9`); «Монтажнику» — `plan.estimate.hint` [PLAN-2] [PLAN-23]. Проверка — тест состава
  `/api/account/access` по системным ролям: «Инженер ИД», «Бухгалтер», «Монтажник» модуля `plan` не
  получают; у «Сметчика» нет `plan.offer.*`

## 3. Схема модуля

- [ ] 3.1 `PlanDbContext : ModuleDbContext` (схема `plan`, `xmin`, 409 при конфликте) и
  `PlanDbContextFactory` без строки подключения [CORE-4]. Проверка — `ModuleSchemaDeclarationTests` и
  `ModuleDbContextInventoryTests` (с `plan`) зелёные
- [ ] 3.2 Миграция `Initial`:
  `dotnet ef migrations add Initial --project src/server/BHS.CRG.Modules.Plan --context PlanDbContext`.
  Проверка — на пересозданной базе старт применяет миграции ядра, затем `plan`
- [ ] 3.3 Дописать `plan` в `ModuleReferenceInventoryTests`, `ModuleFileInventoryTests`, `CoreSectionsTests`.
  Проверка — переписи зелёные
- [ ] 3.4 Команду миграции модуля вписать в `CLAUDE.md` и `AGENTS.md` одним коммитом. Проверка —
  `AgentRulesCopyTests` зелёный

## 4. Живой хост и документы

- [ ] 4.1 `Integration/PlanOnlyHostTests.cs`: хост с `Modules__Enabled=plan` на своей базе поднимается,
  адреса `id` и `costs` отвечают 501 [CORE-1]. Проверка — тест зелёный; ссылка на `Infrastructure` из
  модуля его роняет
- [ ] 4.2 `docs/DEV_NOTES.md`: модуль `plan`, его схема и почему клиентская папка — `features/planning`.
  Проверка — ревью PR

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PlanOnlyHost|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleSchema|FullyQualifiedName~AccountAccess"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
