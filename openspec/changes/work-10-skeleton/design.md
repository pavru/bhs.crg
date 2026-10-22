# Design: каркас модуля `work`

Общая архитектура — `../work-00-epic/design.md`. Здесь только решения этого change.

## Context

Инфраструктура второго контекста построена этапом 2: `ModuleDbContext`, `ModuleSchema`,
`ModuleSchemaMigrator`, `MigrationCensus`, копия схемы. Поэтому каркас и схема идут одним PR, а не тремя,
как A1 + A2a + A2b у `costs` (D1). Образец — PR #1105 (`8fb3af0b`) и #1107 (`9a815985`).

## Goals / Non-Goals

**Goals:** живой хост только с `work`; права первой двери; пустая схема с миграцией `Initial`.

**Non-Goals:** таблицы данных, экраны, порты. Порты едут с первым потребителем (`work-11`).

## Decisions

- **Состав PR.**
  - `BHS.CRG.Modules.Work.csproj` ссылается на `BHS.CRG.Modules` и `BHS.CRG.Domain`.
  - `WorkModule.cs` — по образцу `CostsModule.cs`: код, название, права, `Schema`, `RoutePrefixes = ["/api/work"]`,
    пустые `MapEndpoints` и `InitializeAsync`.
  - `Data/WorkDbContext.cs` (схема `work`, `xmin` как версия строки, 409 при конфликте — как
    `CostsDbContext.cs:79-96`) и `Data/WorkDbContextFactory.cs` без строки подключения.
  - `Migrations/*_Initial`.
- **Право `work.sites.all`** объясняется как «назначать и принимать отчёты на любых стройках». Оно
  заменяет «или администратор» из `WORK-21`. «Администратор» получает его сам: его состав равен
  справочнику прав.
- **Сторож.** `WorkOnlyHostTests` — живой хост с `Modules__Enabled=work` на своей базе (образец
  `CostsOnlyHostTests`). Тест состава `/api/account/access` по системным ролям (D2).
- **Чем ломается.** Обращение модуля к чему-либо помимо контрактов и домена — хост без `id` не
  поднимается. Объявить `work.facts.read` сейчас — тест «Инженер ИД» краснеет.
- **Документы.** Команда `dotnet ef migrations add <Name> --project src/server/BHS.CRG.Modules.Work
  --context WorkDbContext` идёт в `CLAUDE.md` и `AGENTS.md` одним коммитом.

## Risks / Trade-offs

- [Объявление прав молча меняет навигацию ролей] → тест состава доступа по ролям в этом же PR.
