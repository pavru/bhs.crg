# Tasks: живой прогон счетов не ходит к движку распознавания

Один PR, без метки `version:minor` (поведение для человека меняется только в слове причины на стенде без
движка). В PR — «Closes #1344».

## 1. Сервер: файл — первым

- [x] 1.1 `BHS.CRG.Modules/Ports/IModuleRecognition.cs`: `EnsureReadableAsync(content, mimeType, ct)` —
  отказ `RecognitionRefusedException(BadFile)`; реализация в `Api/Modules/Ports/ModuleRecognitionPort.cs`
  через `RecognitionFileCheck.CheckAsync`, сбой окружения — не отказ, а запись в журнал сервера [COST-8].
  Проверка — `ModuleRecognitionPortTests`: нечитаемый PDF — `BadFile`, исправный PDF и PNG — молчание;
  `ModuleBoundaryTests` зелёный
- [x] 1.2 `Modules.Costs/Endpoints/InvoiceScanRecognition.cs`, `StartAsync`: прочитать файл, уходящий
  движку как есть (`InvoiceScanImage.AsIsAsync`, design D1 — образ Excel и Word в запросе не строится),
  и вызвать `EnsureReadableAsync` до `EnsureReadyAsync`; отказ — исход
  `BadFile` записью распознавания, задача не ставится [COST-8]. Проверка — `InvoiceFromScanTests`:
  нечитаемый PDF при ненастроенном движке — `reason: BadFile`, предполётная проверка не вызвана; при
  настроенном — тот же исход, движок не вызван
- [x] 1.3 Доккомментарии `IModuleRecognition` («до постановки — `EnsureReadyAsync`») и
  `InvoiceScanRecognition.StartAsync` — порядок «файл, потом готовность» и почему [COST-8]. Проверка — ревью PR

## 2. Прогон

- [x] 2.1 `src/client/e2e/invoices-smoke.mjs`, проверка 7: сверка отказа о файле — форма «Файл не
  распознан: сам файл не читается», строка «не распознан — файл не читается», на форме нет «не настроено»
  и «недоступно»; тексты — одной константой со ссылкой на `features/costs/recognition.ts`; комментарий
  проверки переписан [COST-8]. Проверка — прогон на стенде без движка зелёный; с откаченным 1.2 — красный
  на этой проверке
- [x] 2.2 Проверки 8 и 8а: комментарий — «содержимое нарочно нечитаемо: исход `BadFile` без запроса к
  движку (#1344)» [COST-8]. Проверка — ревью PR
- [x] 2.3 Проверка 9: `GET /api/settings/integrations`; при включённом и настроенном облачном
  движке распознавания (не Ollama — design D4) нажатие «Распознать файл» пропускается со строкой прогона «движок настроен — запуск не
  проверяется, прогон к движку не ходит (#1344)»; без движка — как прежде [COST-8]. Проверка — прогон на
  стенде с настроенным Gemini: в журнале задач и в колокольчике нет ни одной новой ошибки распознавания
  после прогона; на CI — зелёный
- [x] 2.4 Сверка остальных прогонов: `seed-invoices.mjs`, `invoice-layout-checks.mjs` распознавание не
  запускают — записать это одной строкой в описании PR. Проверка — описание PR
- [x] 2.5 `shared/api/invoiceRecognition.ts`, `sendScan`: вид двойников нового черновика — устаревшим
  (design, Risks: найдено первым прогоном CI). Проверка — проверка 8а прогона зелёная на стенде и в CI

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~InvoiceFromScan|FullyQualifiedName~ModuleRecognitionPort|FullyQualifiedName~BadFileRecognition|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 Живой прогон счетов (`invoices-smoke.mjs`) зелёный в CI
- [x] 3.3 `cd src/client && npx tsc -b` — без ошибок
- [x] 3.4 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.5 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
