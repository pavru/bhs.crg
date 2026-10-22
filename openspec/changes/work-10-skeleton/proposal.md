# Proposal: каркас модуля `work` и его схема

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D1, D2).

## Why

Без каркаса модуля не на чем строить ни назначения, ни отчёты. Каркас первым проверяет то, что у
второго модуля с данными может сломаться молча:
- приложение поднимается только с `work`, без `id` и `costs`;
- права, заранее вписанные в составы ролей (`SystemRoles.cs:59,64,71,117`), после объявления наполняют
  роли и тем самым меняют навигацию.

## What Changes

- Новый проект `BHS.CRG.Modules.Work`: `IAppModule` с кодом `work` и названием «Учёт работ». Пустая группа
  адресов `/api/work` за воротами модуля. Ни одного экрана и ни одной таблицы данных.
- Права первой двери (`WORK-45`, D2) с объяснениями и `ReadAllMark`: `work.report.own`,
  `work.report.review`, `work.assign`, `work.report.read.all`. Новое право `work.sites.all`
  («назначать и принимать на любых стройках», `WORK-46`).
- Права `work.crew.approve`, `work.facts.read`, `work.devices.manage` **не объявляются** — их объявляют
  `work-18`, `work-27` и этап 5.
- `WorkDbContext : ModuleDbContext`, схема `work`, миграция `Initial`, фабрика для `dotnet ef`.
- Включение модуля:
  - вписать в `DeliveredModules.cs`, `BHS.CRG.slnx`, `BHS.CRG.Api.csproj`;
  - перечислить в `deploy/.env.example`;
  - команду миграции модуля — в `CLAUDE.md` и `AGENTS.md` тем же коммитом.
- Модуль различает «справочник не заведён» и «пусто» у «Сотрудника» и «ВидаРаботы»
  (`SkipWhenBlocked` в `CoreRecordTypes.cs:92,148`).

## Capabilities

### New Capabilities
- `work`: модуль учёта работ монтажников — назначения, отчёты, приёмка, бригада, закрытие периода и
  данные для других модулей.

### Modified Capabilities
- Нет.

## Impact

- **Сервер:**
  - новые `src/server/BHS.CRG.Modules.Work/*` и `src/server/BHS.CRG.Tests/Integration/WorkOnlyHostTests.cs`;
  - правки `Api/Modules/DeliveredModules.cs`, `BHS.CRG.Api.csproj`, `BHS.CRG.slnx`.
- **Поставка:** `deploy/.env.example`.
- **Документы:** `CLAUDE.md` и `AGENTS.md` (`AgentRulesCopyTests`), `docs/DEV_NOTES.md`.
