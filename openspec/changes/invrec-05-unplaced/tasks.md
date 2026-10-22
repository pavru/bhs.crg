# Tasks: «прочитано, в счёт не положено» — с причиной, всегда

## 1. PR 1: сервер — сбор и хранение

- [x] 1.1 `Endpoints/InvoiceScanUnplaced.cs`: запись `UnplacedRead` (место, номер строки, ключ, значение,
  код причины, подробность) и шесть кодов причин [COST-8]. Проверка — `InvoiceScanUnplacedTests`
  компилируется и покрывает каждый код
- [x] 1.2 `InvoiceScanHeader.Of` отдаёт записи `no-field`, `not-recognizable`, `duplicate-key` вместо трёх
  текстовых замечаний; `Input`-цели молча пропускаются, как прежде [COST-8]. Проверка —
  `InvoiceScanHeaderTests` на записях, а не на текстах
- [x] 1.3 `InvoiceScanLines.Read` отдаёт записи `no-field`/`duplicate-key` с номером строки файла вместо
  `StrayNote`; примечание строки «В файле не прочитано» не меняется [COST-8]. Проверка —
  `InvoiceScanLineKeysTests`, `InvoiceScanLinesTests`
- [x] 1.4 `InvoiceScanReading.ApplyAsync`/`GuardedAsync`: записи `unparsed`, `occupied` (значение
  отличается), `guard` с текстом отказа; записи строк — до ветвления по строкам; замечания «впишите
  вручную» и «охрана записи» остаются [COST-8]. Проверка — `InvoiceScanUnplacedTests`: список хранится
  при легших строках, при строках в предложении (свои строки; режим человека) и при `RowsProblem`
- [x] 1.5 `InvoiceRecognition.Unplaced` (`jsonb`, `null` — до change) и `Finish(…, unplaced)`; миграция
  `dotnet ef migrations add InvoiceRecognitionUnplaced --project src/server/BHS.CRG.Modules.Costs
  --context CostsDbContext` [COST-8]. Проверка — на пересозданной базе старт применяет миграции ядра и
  `costs`; `BackupManifestCoverageTests` зелёный
- [x] 1.6 Вид распознавания (`InvoiceScanRecognition.cs`) отдаёт `unplaced`; тип клиента в
  `shared/api/invoiceRecognition.ts` дополнен полем (без показа) [COST-8]. Проверка —
  `InvoiceFromScanTests.Targets`: колонка с опечаткой в ключе приходит записью с `no-field` и номерами
  строк; `npx tsc -b`

## 2. PR 2: клиент — показ

- [x] 2.1 Три варианта макета свёрнутой строки и таблицы в Claude Design по разбору Дизайнера от
  18.10.2026; выбор владельца; ссылку вписать в `design.md` (`docs/UI.md`, «Новый экран: порядок»).
  Проверка — ссылка и выбранный вариант в `design.md`
- [x] 2.2 `InvoiceRecognitionNote.tsx`: строка «Ещё прочитано, в счёт не положено: N — Показать» без
  окраски предупреждения; «Показать» раскрывает таблицу общим компонентом «где — ключ — значение —
  причина»; слова причин по коду; блок замечаний показывается и при одном непустом списке [COST-8].
  Проверка — компонентный тест: число, раскрытие, `null` и `[]` — строки нет
- [x] 2.3 `docs/ADMIN_GUIDE.md`, профиль «Счёт на оплату»: где в счёте видно, что колонка или поле не
  легли, и что значит каждая причина (оглавление — `cd docs/tools && npm run toc`, если менялись главы).
  Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~InvoiceScan|FullyQualifiedName~InvoiceFromScan|FullyQualifiedName~BackupManifest|FullyQualifiedName~ModuleDbContextInventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 Живые прогоны со счётом из скана (`invoices-smoke`, `invoice-lines-smoke`) зелёные
- [ ] 3.6 Оба PR — метка `version:minor`; PR 2 — «Closes #1484»
