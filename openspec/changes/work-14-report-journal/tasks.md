# Tasks: журнал отчёта только на дописывание

## 1. Хранение и единственное место записи

- [ ] 1.1 `Data/ReportJournalEntry.cs` (`IAppendOnly`), колонка `reports.submitted_at`, миграция
  `dotnet ef migrations add ReportJournal --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`
  [WORK-12] [WORK-51]. Проверка — на пересозданной базе миграция применяется; тест: правка и удаление
  загруженной записи отвергаются на сохранении
- [ ] 1.2 `Data/ReportJournal.cs`: сравнение до и после, `changes` по полям, автор и исполнитель снимком,
  `entered_for`, оба времени; вызов из пути сохранения `work-13` в том же `SaveChanges` [WORK-11]
  [WORK-31] [WORK-51] [CORE-28]. Проверка — тесты: правка объёма 120 → 150 даёт запись с полем и
  значениями; сбой сохранения не оставляет ни правки, ни записи; менеджер за сотрудника — `enteredFor: true`
- [ ] 1.3 Правило `after_submission` по времени устройства и `submitted_at` [WORK-11]. Проверка — тесты с
  `submitted_at`, поставленным в базе: правка после момента — `true`; правка до момента, доставленная
  позже, — `false`
- [ ] 1.4 Переписи `WorkJournalInventoryTests` (одно место записи) и `WorkJournalWritePathTests`
  (`ExecuteUpdate`, `ExecuteDelete`, сырой SQL по `report_journal`) [WORK-12] [CORE-3]. Проверка — оба
  теста зелёные; вставка `ExecuteDelete` по журналу в модуль роняет второй с названием файла

## 2. Чтение и журнал ядра

- [ ] 2.1 `GET /api/work/reports/{id}/journal` через параметр доступа `work-07`, политика «любое из»
  `work.report.own`, `work.report.review`, `work.report.read.all` [WORK-39] [WORK-54] [AUTH-8] [AUTH-9].
  Проверка — тесты: чужой отчёт — 404; менеджер чужой стройки — 404; черновик у менеджера — 404, а введённый им самим — 200;
  `read.all` — 200; без прав модуля — отказ ворот; `EndpointGateInventoryTests` зелёный
- [ ] 2.2 Дубль в журнал ядра: `work.report.deleted`, `work.report.edited-after-submission` через
  `IModuleActivityLog` после сохранения [CORE-28]. Проверка — тест: удаление черновика оставляет запись
  ядра; сбой журнала ядра не откатывает удаление; `ActivityLogInventoryTests` зелёный
- [ ] 2.3 Копия и восстановление сохраняют журнал [CORE-28] [CORE-26]. Проверка — интеграционный тест
  копии схемы `work`: после восстановления записи журнала совпадают; `BackupManifestCoverageTests` зелёный
- [ ] 2.4 `docs/USER_GUIDE.md`: «правка после подачи», почему журнал не правится и что значит «вводит
  Иванов за Петрова»; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend» (содержание) зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkJournal|FullyQualifiedName~ReportJournal|FullyQualifiedName~ActivityLogInventory|FullyQualifiedName~BackupManifestCoverage|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
