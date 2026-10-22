# Proposal: каркас модуля `plan` и его схема

Дочерний change зонтика `plan-00-epic`, общая архитектура — `../plan-00-epic/design.md` (D1, D12).

## Why

Без каркаса модуля не на чем строить ни сметы, ни КП, ни графики. Каркас первым проверяет то, что у
третьего модуля с данными может сломаться молча:
- приложение поднимается только с `plan`, без `id` и `costs`;
- права, заранее вписанные в составы ролей (`Api/Auth/SystemRoles.cs:77,100`), после объявления
  наполняют роли и тем самым меняют навигацию.

Составы ролей расходятся с `PLAN-23`: у «Сметчика» стоит `plan.offer.edit`, хотя КП — дело «Менеджера
проекта», а «сметчик без права на КП не видит цен продажи» (`PLAN-2`). Расхождение исправляется до
того, как право объявят: после объявления оно стало бы правом живых пользователей.

## What Changes

- Новый проект `BHS.CRG.Modules.Plan`: `IAppModule` с кодом `plan` и названием «Планирование». Пустая
  группа адресов `/api/plan` за воротами модуля. Ни одного экрана и ни одной таблицы данных.
- Права первой двери с объяснениями и `ReadAllMark`: `plan.estimate.read`, `plan.estimate.edit`.
  Остальные права `PLAN-23` объявляют change, открывающие их первый адрес (`design.md` зонтика, D12).
- Составы системных ролей приводятся к `PLAN-23`:
  - «Сметчик» — `plan.estimate.read`, `plan.estimate.edit`, `plan.estimate.cost`, без `plan.offer.edit`;
  - «Менеджер проекта» — права КП, прайс-листа, графиков, `plan.labor.read` и `plan.estimate.read` (решение В-1, `PLAN-Q9`);
  - «Монтажник» — `plan.estimate.hint`;
  - коды, которые ещё не объявлены, пропускаются при наполнении, как сегодня.
- `PlanDbContext : ModuleDbContext`, схема `plan`, миграция `Initial`, фабрика для `dotnet ef`.
- Включение модуля: `DeliveredModules.cs`, `BHS.CRG.slnx`, `BHS.CRG.Api.csproj`, `deploy/.env.example`;
  команда миграции модуля — в `CLAUDE.md` и `AGENTS.md` тем же коммитом.

## Capabilities

### New Capabilities
- `plan`: модуль планирования — сметы и их редакции, импорт из ГРАНД-Сметы, распределение по разделам,
  коммерческие предложения, прайс-лист, графики с календарями и данные для других модулей.

### Modified Capabilities
- Нет.

## Impact

- **Сервер:**
  - новые `src/server/BHS.CRG.Modules.Plan/*` и `src/server/BHS.CRG.Tests/Integration/PlanOnlyHostTests.cs`;
  - правки `Api/Modules/DeliveredModules.cs`, `Api/Auth/SystemRoles.cs`, `BHS.CRG.Api.csproj`, `BHS.CRG.slnx`;
  - переписи, перечисляющие модули поимённо: `ModuleDbContextInventoryTests`, `ModuleReferenceInventoryTests`,
    `ModuleFileInventoryTests`, `CoreSectionsTests`, `ModuleBoundaryTests`.
- **Поставка:** `deploy/.env.example`.
- **Документы:** `CLAUDE.md` и `AGENTS.md` (`AgentRulesCopyTests`), `docs/DEV_NOTES.md`.
