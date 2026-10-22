# Design: каркас модуля `plan`

Общая архитектура — `../plan-00-epic/design.md` (D1, D12). Здесь только решения этого change.

## Context

Инфраструктура модульного контекста построена этапом 2: `ModuleDbContext`, `ModuleSchemaMigrator`
(`Api/Modules/ModuleSchemaMigrator.cs:32`), копия схемы модуля. Образец объявления — `CostsModule.cs`,
контекста — `CostsDbContext.cs` и `CostsDbContextFactory.cs`, живого хоста — `CostsOnlyHostTests.cs:35`.
Модули перечисляет `DeliveredModules.All()` (`Api/Modules/DeliveredModules.cs:24`). Переписи, которые
перечисляют модули поимённо и требуют дописать `plan`: `ModuleDbContextInventoryTests.cs:34-41`,
`ModuleReferenceInventoryTests.cs:25-28`, `ModuleFileInventoryTests.cs:33`, `CoreSectionsTests.cs:21`,
`ModuleBoundaryTests.cs:109`; остальные находят проекты `BHS.CRG.Modules.*` сами (`SolutionModules.cs`).

## Goals / Non-Goals

**Goals:** живой хост только с `plan`; права первой двери; составы ролей по `PLAN-23`; пустая схема с
миграцией `Initial`.

**Non-Goals:** таблицы данных, экраны, порты. Порты и таблицы едут с первым потребителем (`plan-03`,
`plan-04`, `plan-06`).

## Decisions

- **Состав PR.**
  - `BHS.CRG.Modules.Plan.csproj` ссылается на `BHS.CRG.Modules` и `BHS.CRG.Domain`.
  - `PlanModule.cs` — по образцу `CostsModule.cs`: код, название, права, `Schema`,
    `RoutePrefixes = ["/api/plan"]`, пустые `MapEndpoints` и `InitializeAsync`.
  - `Data/PlanDbContext.cs` (схема `plan`, `xmin` как версия строки, 409 при конфликте — как
    `CostsDbContext`) и `Data/PlanDbContextFactory.cs` без строки подключения.
  - `Migrations/*_Initial`.
- **Права первой двери.** `plan.estimate.read` — «сметы стройки: строки, объёмы, редакции без денег»
  (деньги — `plan.estimate.cost`, `plan-06`); `plan.estimate.edit` — «импорт, сопоставление,
  распределение, действующая редакция». `ReadAllMark.In` у чтения, `Out` с причиной у правки.
- **Составы ролей.** `SystemRoles.cs`:
  - «Сметчик» — `plan.estimate.read`, `plan.estimate.edit`, `plan.estimate.cost`; `plan.offer.edit`
    убирается;
  - «Менеджер проекта» — `plan.offer.*`, `plan.pricelist.edit`, `plan.schedule.*`, `plan.labor.read` и, по
    рекомендации В-1, `plan.estimate.read` (ждёт решения В-1);
  - «Снабженец» сохраняет `plan.estimate.materials`, «Монтажник» получает `plan.estimate.hint`.
  Не объявленные ещё коды пропускаются при наполнении: роль дописывается один раз, а не в каждом change.
- **Сторож.** `PlanOnlyHostTests` — живой хост с `Modules__Enabled=plan` на своей базе; число прав `plan.`
  зашито в тест с объяснением, как в `CostsOnlyHostTests`. Тест состава `/api/account/access` по
  системным ролям: «Инженер ИД», «Бухгалтер», «Монтажник» не получают модуль `plan`.
- **Чем ломается.** Ссылка модуля на `Infrastructure` или `Modules.Costs` — `ModuleBoundaryTests`
  красный. `plan.offer.edit` у «Сметчика» — тест состава краснеет, как только `plan-13` объявит право.
- **Документы.** Команда `dotnet ef migrations add <Name> --project src/server/BHS.CRG.Modules.Plan
  --context PlanDbContext` идёт в `CLAUDE.md` и `AGENTS.md` одним коммитом.

## Risks / Trade-offs

- [Объявление прав молча меняет навигацию ролей] → тест состава доступа по ролям в этом же PR.
- [В-1 решится иначе] → правка одной строки состава роли и теста; модуль от неё не зависит.
