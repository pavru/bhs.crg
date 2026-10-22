# Tasks: закрытие периода в учёте работ

## 1. PR 1: ядро — запрещающие строки (базовый тип версии — `plan-12`)

- [ ] 1.1 `IModuleClosingReport.cs`: `ModuleClosingSection.Blocking`; `ClosingReport.cs` и
  `ModuleClosingReports.cs` переносят его в перечень и отпечаток [CORE-35] [WORK-52]. Проверка —
  `PeriodClosureTests`: отпечаток меняется, когда меняется запрещающая строка
- [ ] 1.2 `PeriodClosureService.cs`: ненулевая запрещающая строка — 409 под замком, без записи и журнала;
  `PeriodEndpoints.cs`: `blocking` и `canClose` в предпросмотре [CORE-35]. Проверка — `PeriodClosureTests`
  на пробном модуле: предпросмотр `canClose: false`, закрытие 409, записи о закрытии нет; с одним
  `costs` — без изменений; `PeriodClosureInventoryTests` зелёный
- [ ] 1.3 Макеты раунда Р4 — диалог закрытия с запрещающими строками: три варианта, выбор владельца
  ссылкой в `design.md` [CORE-35]. Проверка — ссылка вписана до начала 1.4
- [ ] 1.4 Клиент: `features/periods/ClosingSections.tsx` — блок запрещающих строк; `PeriodsPage.tsx` —
  «Закрыть» заперта с причиной; правило ядра «граница — по поясу компании»; `shared/api/periods.ts`
  [CORE-35]. Проверка — vitest `closing.test.ts`: `canClose: false` запирает кнопку и называет строку
- [ ] 1.5 ~~Базовый тип версии и перепись~~ — строит этап 3 (`plan-12-record-versions`, D18 зонтика); здесь
  только проверить, что `RecordVersionInventoryTests` видит наследника `work` [CORE-36]. Проверка —
  перепись зелёная с отчётом `work` среди наследников

## 2. PR 2: модуль `work`

- [ ] 2.1 `Closing/WorkClosingReport.cs`: правило даты по поясу стройки; «в окне подачи: N» в
  `Blocking`; «ждут приёмки: N» тоже в `Blocking` [WORK-59]; принятые — замороженное [WORK-15] [WORK-52] [CORE-35] [COST-24].
  Проверка — `WorkClosingReportTests`: стройка западнее компании блокирует закрытие октября; поданный непринятый отчёт блокирует; ночная
  смена 31.10, доставленная 01.11, — в октябре
- [ ] 2.2 Отказ правки отчёта закрытого периода во всех пишущих адресах, раньше `If-Match`; приёмка,
  возврат и ввод задним числом — тоже 409 [WORK-15] [WORK-59]. Проверка — `ReportClosedPeriodTests`; адреса вписаны в
  исключения порядка отказов с причиной
- [ ] 2.3 `Data/ReportVersion.cs : ModuleRecordVersion`, миграция
  `dotnet ef migrations add ReportVersions --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`
  [CORE-36]. Проверка — на пересозданной базе старт применяет миграцию; `RecordVersionInventoryTests` видит наследника
- [ ] 2.4 `POST /api/work/reports/{id}/correction`: причина, всё содержимое, `If-Match`,
  `work.report.review` на стройке отчёта, ворота `work`; версия, содержимое и журнал одним `SaveChanges`
  [WORK-15] [CORE-36] [AUTH-8] [AUTH-9]. Проверка — `ReportCorrectionTests`: без `If-Match` — 400,
  устаревшая — 409, без причины — 400, монтажник — 403, чужая стройка — 404, выключенный модуль —
  отказ модуля, открытый период — 409 «не нужно»; `EndpointGateInventoryTests` зелёный
- [ ] 2.5 Ответ отчёта: `versions` и `view` (`corrected` | `at-closing`) под изоляцией [WORK-15]
  [CORE-36] [WORK-39]. Проверка — тест: `at-closing` отдаёт версию на момент записи закрытия; чужой
  отчёт — 404
- [ ] 2.6 Досылка к принятому в открытом периоде: статус «подан», пометка `accepted-changed`, журнал
  называет изменение; код в каталоге пометок [WORK-16]. Проверка — тест досылки; сторож «каталог
  сервера = каталог клиента» (`work-16`) зелёный с новым кодом
- [ ] 2.7 `docs/USER_GUIDE.md`: закрытие периода при учёте работ (почему открытое окно запрещает,
  пояс стройки и компании), исправление отчёта, досылка; `cd docs/tools && npm run toc`. Проверка —
  проверка «Frontend» (содержание) зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PeriodClosure|FullyQualifiedName~RecordVersionInventory|FullyQualifiedName~WorkClosingReport|FullyQualifiedName~ReportCorrection|FullyQualifiedName~ReportClosedPeriod|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
