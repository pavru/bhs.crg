# Tasks: первый старт без истории запусков — не пропущенная копия

Функциональный PR (bug) — без метки `version:minor`, в описании `Closes #1403`.

## 0. Решение владельца

- [ ] 0.1 Решение владельца по В1 (design, «Открытые вопросы»: А / Б / В) — до кода. Выбор записать в
  design.md (раздел Decisions) и, если выбран не А, переписать дельту `specs/core/spec.md` и задачи
  ниже. Проверка — решение записано в design.md

## 1. Правило и служба (вариант А)

- [ ] 1.1 `BackupScheduleState.CountingSince`; `IsDue` и `NextRunAt` считают от
  `lastRunAt ?? countingSince`, при обоих `null` — от «сейчас» (D1, D2) [CORE-25]. Проверка —
  `BackupScheduleServiceTests`:
  - `NeverRun_DueOnceTheHourPassed` заменён на «пустая история после срока — не пора, ближайшая
    завтра»;
  - новый «пустая история до срока — сегодня в срок»;
  - прежние тесты пропущенной ночи, недели простоя и перевода часов зелёные без правки
- [ ] 1.2 `TickAsync`: нет ни `LastRunAt`, ни `CountingSince` — записать `CountingSince = now`, задачу
  не ставить; при выключенном расписании не писать (D2, D3) [CORE-25]. Проверка — тест службы на
  подставных часах (как `BackupScheduleZoneTests`): пустое состояние — задач нет, начало отсчёта записано; через сутки в срок —
  задача поставлена
- [ ] 1.3 `BackupEndpoints`: строка «Ближайшая копия» берёт `CountingSince` [CORE-25]. Проверка —
  интеграционный тест: на пустом состоянии после срока `nextRun.due == false`, дата — завтра
- [ ] 1.4 Доккомментарий `IsDue` переписать: прежний довод «не ждать до завтрашней ночи» снят, причина
  — #1403. `docs/ADMIN_GUIDE.md`, «Резервное копирование: как снять, скачать и восстановить»: первая копия после установки или
  восстановления — в ближайший срок, ручная доступна сразу. Проверка — ревью PR, `npm run toc` в
  `docs/tools` не меняет оглавление

## 2. Верификация

- [ ] 2.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~BackupSchedule|FullyQualifiedName~BackupScheduler|FullyQualifiedName~BackupManifest"` — зелёный
- [ ] 2.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [ ] 2.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 2.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
