# Tasks: закрытие периода в учёте работ

Серверный PR объединяет 1.1, 1.2, 1.5 и раздел 2. Задачи 1.3 и 1.4 остаются следующему PR после
выбора макета Р4.

## 1. PR 1: ядро — запрещающие строки (базовый тип версии — `plan-12`)

- [x] 1.1 `IModuleClosingReport.cs`: `ModuleClosingSection.Blocking`; `ClosingReport.cs` и
  `ModuleClosingReports.cs` переносят его в перечень и отпечаток [CORE-35] [WORK-52]. Проверка —
  `PeriodClosureTests`: отпечаток меняется, когда меняется запрещающая строка
- [x] 1.2 `PeriodClosureService.cs`: ненулевая запрещающая строка — 409 под замком, без записи и журнала;
  `PeriodEndpoints.cs`: `blocking` и `canClose` в предпросмотре [CORE-35]. Проверка — `PeriodClosureTests`
  на пробном модуле: предпросмотр `canClose: false`, закрытие 409, записи о закрытии нет; с одним
  `costs` — без изменений; `PeriodClosureInventoryTests` зелёный
- [ ] 1.3 Макеты раунда Р4 — диалог закрытия с запрещающими строками: три варианта, выбор владельца
  ссылкой в `design.md` [CORE-35]. Проверка — ссылка вписана до начала 1.4
- [ ] 1.4 Клиент: `features/periods/ClosingSections.tsx` — блок запрещающих строк; `PeriodsPage.tsx` —
  «Закрыть» заперта с причиной; правило ядра «граница — по поясу компании»; `shared/api/periods.ts`
  [CORE-35]. Ссылка на очередь требует экрана `work-21` и совпадения чисел с учётом отбора и охвата.
  Проверка — vitest `closing.test.ts`: `canClose: false` запирает кнопку и называет строку
- [x] 1.5 ~~Базовый тип версии и перепись~~ — строит этап 3 (`plan-12-record-versions`, D18 зонтика); здесь
  только проверить, что `RecordVersionInventoryTests` видит наследника `work` [CORE-36]. Проверка —
  перепись зелёная с отчётом `work` среди наследников

## 2. PR 2: модуль `work`

- [x] 2.1 `Closing/WorkClosingReport.cs`: правило даты по поясу стройки; «в окне подачи: N» в
  `Blocking`; «ждут приёмки: N» и «возвращены на правку: N» — отдельные строки в `Blocking`
  [WORK-59]; принятые — замороженное [WORK-15] [WORK-52] [CORE-35] [COST-24].
  Проверка — `WorkClosingReportTests`: стройка западнее компании блокирует закрытие октября, ночная
  смена 31.10, доставленная 01.11, учитывается в октябре; возврат блокирует, повторная подача
  переносит отчёт в очередь без двойного счёта, приёмка снимает запрет
- [x] 2.2 Отказ правки отчёта закрытого периода во всех пишущих адресах, раньше `If-Match`; приёмка,
  возврат, ввод задним числом, фото и автоподача охраняются тем же замком [WORK-15] [WORK-59].
  Доставка фото, объявленного до закрытия, разрешена без правки [WORK-11] [WORK-34].
  Проверка — `ReportClosedPeriodTests` и `ReportProtocolTests`: отказы, доставка и её повтор,
  повтор правки при закрытой прежней дате назначения, обязательный заголовок при повторе удаления.
  Адреса вписаны в исключения порядка отказов с причиной
- [x] 2.3 `Data/ReportVersion.cs : ModuleRecordVersion`, миграция
  `dotnet ef migrations add ReportVersions --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`
  [CORE-36]. Объявить справочные ссылки снимка и автора. Проверка — на пересозданной базе старт
  применяет миграцию, прежний откат фото сохраняет отчёт, `RecordVersionInventoryTests` видит наследника.
  Отдельная миграция помнит закрытие при объявлении и доставке фото, сохраняя прежние записи
- [x] 2.4 `POST /api/work/reports/{id}/correction`: причина, всё содержимое, `If-Match`,
  `work.report.review` на стройке отчёта, ворота `work`; версия, содержимое и журнал одним `SaveChanges`
  [WORK-15] [CORE-36] [AUTH-8] [AUTH-9]. Проверка — `ReportCorrectionTests`: без `If-Match` — 400,
  устаревшая — 409, без причины — 400, монтажник — 403, чужая стройка — 404, выключенный модуль —
  отказ модуля, открытый период — 409 «не нужно»; `EndpointGateInventoryTests` зелёный
- [x] 2.5 Порт ядра сообщает начало непрерывного закрытия дня, ответ отчёта несёт `versions` и
  `view` (`corrected` | `at-closing`) под изоляцией [WORK-15] [CORE-36] [WORK-39]. Проверка —
  `at-closing` отдаёт версию на момент записи закрытия с учётом отмены и повторного закрытия;
  поздно доставленное фото всё ещё ожидается при совпадающих отметках времени; чтение под двумя
  замками защищено от одновременного исправления и открытия; чужой отчёт — 404
- [x] 2.6 Досылка к принятому в открытом периоде: статус «подан», пометка `accepted-changed`, журнал
  называет изменение и сравнивает время досылки с исходной подачей; код в каталоге пометок
  [WORK-16]. Проверка — тесты досылки до и после исходной подачи; сторож «каталог
  сервера = каталог клиента» (`work-16`) зелёный с новым кодом
- [x] 2.7 `docs/USER_GUIDE.md`: закрытие периода при учёте работ (почему открытое окно запрещает,
  пояс стройки и компании), исправление отчёта, досылка; `cd docs/tools && npm run toc`. Проверка —
  проверка «Frontend» (содержание) зелёная

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PeriodClosure|FullyQualifiedName~RecordVersionInventory|FullyQualifiedName~WorkClosingReport|FullyQualifiedName~ReportCorrection|FullyQualifiedName~ReportClosedPeriod|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
