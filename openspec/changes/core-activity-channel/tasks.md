# Tasks: канал действия в журнале — интерфейс или агент через MCP

Функциональный PR — метка `version:minor`, в описании `Closes #1336`. `core-mcp-gate-inventory`, давший
опись меняющих инструментов для сторожа, закрыт (#1992, архив #1995); план пересверен с master 2.128.3
16.11.2026.

## 0. Решение владельца

- [x] 0.1 (22.10.2026: вариант А — одна запись на удавшийся вызов меняющего инструмента MCP; записано в design и в #1336) Решение владельца по В1 (design, «Открытые вопросы»: А / Б / В) — до кода. Выбор записать в
  design.md (D4) и в комментарий к #1336 («первым шагом — решить и записать»); при выборе не А —
  переписать требование «Действие агента через MCP видно в журнале» и раздел 3. Проверка — решение
  записано

## 1. Канал в записи и в контексте

- [x] 1.1 `ActivityRecord.Channel` (`ui` / `mcp` / `system`, строкой); миграция ядра
  `dotnet ef migrations add ActivityChannel --project src/server/BHS.CRG.Infrastructure
  --startup-project src/server/BHS.CRG.Api --context AppDbContext` с заполнением по автору (D5)
  [CORE-28.1]. Проверка — тест миграции: запись без автора — `system`, с автором — `ui`; на
  пересозданной базе старт применяет миграцию
- [x] 1.2 `ActivityActor` с каналом; `HttpContextActivityActor` — метаданные адреса, иначе `ui`, без
  запроса — `system` (и у автора `SystemActivityScope`); метка `ActivityChannel.Mcp` на `MapMcp` (D1, D2)
  [CORE-28.1]. Проверка — тест: смена роли из интерфейса — `ui`; запись при старте — `system`
- [x] 1.3 `Job.Channel` (миграция той же командой, имя `JobChannel`); `JobService.EnqueueAsync`
  заполняет его; `JobBackgroundService` открывает окружающий источник автора задачи `JobActivityScope`
  (`AsyncLocal` приёмом `SystemActivityScope`, `using` на задачу), который читает только журнал (D3)
  [CORE-28.1]. Проверка — тест: задача, поставленная через `/mcp`, несёт `mcp`, её запись журнала —
  заказчик с пометкой задачи и `mcp`, а `IActivityActor.Current` и `IModuleUser.Id` в её области — система;
  следующая задача без заказчика пишет от `System`
- [x] 1.4 Копия: `BackupActivityRecord.Channel` (необязательное, как `SessionId`); импорт без поля —
  правило D5 (D6) [CORE-28] [CORE-26]. Проверка — `BackupManifestCoverageTests` зелёный; тест: копия со
  старой записью без канала восстанавливается с выведенным каналом
- [x] 1.5 Канал рвёт сессию: `ActivitySessionTail.Channel`, `ActivitySession.Continues` сравнивает канал
  (D9) [CORE-28.2]. Проверка — `ActivitySessionTests`: тот же автор, цель и группа, другой канал — новая
  сессия

## 2. Отбор

- [x] 2.1 `ReadAsync` / `CountAsync` с `channel` (отбор рядов по голове); `GET /api/activity?channel=` —
  400 на неизвестное значение; канал в ряду (`ActivityRow`) и в записях серии (D7) [CORE-28]. Проверка —
  интеграционные тесты: `channel=mcp` отдаёт только ряды `mcp` и считает ряды по тому же отбору;
  `channel=x` — 400; без `core.audit.read` — 403
- [x] 2.2 `ActivityPage.tsx`: второй отбор (`Select variant="chip"`, `emptyItem` «Все каналы») и пометка
  `StatusText` «через агента (MCP)» в колонке «Кто»; тип ряда, записи и параметр — в
  `shared/api/activity.ts` (D8) [CORE-28]. Проверка — vitest на хук
  отбора; скриншот экрана с отбором `mcp` в описании PR

## 3. Действие агента (вариант А)

- [x] 3.1 Действие `core.mcp.tool.called` в `ActivityActions` (и в `All`); фильтр вызовов
  MCP-сервера (`WithRequestFilters` → `AddCallToolFilter`) рядом с `AddAuthorizationFilters()`: удавшийся вызов меняющего инструмента — запись с
  целью из аргументов и названием инструмента; отказ, исключение, `IsError` — без записи (D4)
  [CORE-23] [CORE-28]. Проверка — задачи 3.2, 3.3
- [x] 3.2 Сторож: каждый меняющий инструмент из описи (`McpInventory.FromHost`, `Tool.ReadOnly == false`)
  через фильтр даёт запись `mcp` со своим названием, читающий — ни одной [CORE-23] [CORE-28]. Проверка — тест зелёный;
  снятие фильтра из регистрации делает его красным с перечнем инструментов
- [x] 3.3 Сквозной тест через `/mcp`: `report_observation` с токеном сотрудника — запись `mcp`, автор —
  этот сотрудник; вызов с отказом прав — записи нет [CORE-23] [CORE-28]. Проверка — тест зелёный
- [x] 3.4 `docs/ADMIN_GUIDE.md` §10 «Доступ внешних агентов (MCP)»: действия агента видны в журнале,
  отбор по каналу; `docs/tz/10-ядро.md`, `CORE-28.1`: канал записи. Проверка — ревью PR; `npm run toc`
  в `docs/tools` — без расхождений

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Activity|FullyQualifiedName~Mcp|FullyQualifiedName~Job|FullyQualifiedName~Backup"` — зелёный
- [x] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [x] 4.3 `cd src/client && npm test` — зелёный; `npm run lint:ratchet` — без роста
- [x] 4.4 `cd src/client && npm run ui:ratchet` — без роста
- [x] 4.5 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
