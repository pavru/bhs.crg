# Tasks: проверка повтора файла по отпечатку до выгрузки

## 0. До кода

- [ ] 0.1 Решения листа «Ф» от 27.10.2026 перенесены без изменений: повтор спрашивают до выгрузки, по
  отпечатку браузера; 409 при выгрузке отвергнут; сознательный повтор помечается на сервере (зонтик
  D7). [COST-6.7] Проверка — `design.md` называет их в D2, D5 и Non-Goals.

## 1. PR 1 — вопрос по отпечаткам, поля двойника, «кто завёл»

- [ ] 1.1 `InvoiceSameFiles.ByFingerprintsAsync` (один `IN` по индексу, доверие печати, `Limit` на
  отпечаток); `FindAsync` зовёт его (design D1). [COST-6.7] Проверка — `InvoiceSameFileTests`
  прежние зелёные; новый тест: недоверенная печать не попадает ни в `sameFile`, ни в ответ по
  отпечатку.
- [ ] 1.2 `Invoice.Create(typeId, user.Id, user.Name)`, колонка `created_by_name`; миграция
  `dotnet ef migrations add InvoiceCreatedByName --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext`;
  ставится в `from-scan` и `POST /api/costs/invoices`. [COST-6.7] Проверка — миграция применяется на
  пересозданной базе; тест: имя совпадает с вошедшим, после переименования двойник называет прежнее.
- [ ] 1.3 Поля двойника: поставщик (`RefsAsync` одним вызовом, только при непустом перечне), `State`,
  `Payment`, `CreatedByName`, `Mine` (design D3). [COST-6.7] Проверка — тесты: двойник разобран и
  оплачен другим → поля и `mine: false`; свой черновик → `mine: true`; справочник отказал (поддельный
  порт) → `SupplierId` без названия; без двойников справочник не зовётся (счётчик вызовов в поддельном
  порте).
- [ ] 1.4 `GET /api/costs/invoices/same-file` в группе счетов под `costs.invoice.read` (design D2).
  [COST-6.7] Проверка — интеграционные тесты: три отпечатка → три записи по порядку; 0, 51 и не-hex →
  400; верхний регистр принят; `costs.waybill.read` без `costs.invoice.read` → 403; пользователь без
  прав модуля → отказ; перепись ворот (AUTH-9) находит адрес; запрос с 50 отпечатками проходит через
  nginx стенда (`https://localhost/api/...`) — 200.
- [ ] 1.5 Общий набор примеров отпечатка (файл → hex: пустой, 1 байт, 55/56/63/64/65 байт, ~5 МБ из
  генератора) — JSON рядом с клиентской утилитой ребёнка 14 или в `src/client/src/shared/utils`;
  серверный тест сверяет `FingerprintAsync` с ним (по образцу `InvoiceLineCasesTests`). [COST-6.7]
  Проверка — тест зелёный; набор назван в описании PR для 14.
- [ ] 1.6 Типы клиента: `InvoiceSameFile` с новыми полями, функция `sameFileByFingerprints(prints)` в
  `shared/api` (без экрана). [COST-6.7] Проверка — `npx tsc -b`; vitest функции: параметры
  повторяются, ответ разбирается.
- [ ] 1.7 Копия: счёт с `created_by_name` возвращается из копии тем же (`InvoiceBackupRoundTripTests`);
  `BackupManifestCoverageTests` зелёный. [COST-6.7] Проверка — тесты зелёные.
- [ ] 1.8 `docs/DEV_NOTES.md` (D4 #1093, «Тот же файл у другого счёта»): вопрос по отпечаткам до
  выгрузки; для вопроса хеш считает браузер, для записи — сервер; «предупреждение о дублях — отложено»
  закрыто. [COST-6.7] Проверка — ревью PR.

## 2. PR 2 — сознательный повтор

- [ ] 2.1 Колонка `scan_repeat_accepted` (отпечаток, повтор которого принят); миграция
  `dotnet ef migrations add InvoiceScanRepeatAccepted --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext`.
  [COST-6.7] Проверка — миграция применяется на пересозданной базе.
- [ ] 2.2 `from-scan` принимает `acceptRepeat`; ставит признак только при доверенных двойниках (design
  D5); `InvoiceView.ScanRepeatAccepted = ScanRepeatAccepted == TrustedFingerprint`. [COST-6.7]
  Проверка — тесты: повтор с `acceptRepeat` → `true`; без двойников → `false`, без отказа; замена файла
  через `POST …/{id}/scan` → `false`; без поля → `false`.
- [ ] 2.3 Ворота и версия у `from-scan`: `costs.invoice.read` без `costs.invoice.edit` → 403, черновика
  нет; без прав модуля → отказ; без `If-Match` → 201 (названное исключение, design D6);
  `InvoiceStaleFormTests` зелёный без новой записи в `WritesNothing`. [COST-6.7] Проверка — тесты
  отказов зелёные.
- [ ] 2.4 Копия: копия старше колонки не делает отпечаток «принятым» у счёта, чей файл в копии другой.
  Проверка — сценарий в `InvoiceBackupRoundTripTests` или `ModuleBackupShapeTests`;
  `BackupManifestCoverageTests` зелёный. [COST-6.7]
- [ ] 2.5 Тип клиента `scanRepeatAccepted?`; `sendScan` умеет `acceptRepeat` (без экрана). [COST-6.7]
  Проверка — `npx tsc -b`; vitest: поле формы уходит только при `true`.

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~InvoiceSameFile|FullyQualifiedName~InvoiceFromScan|FullyQualifiedName~InvoiceStaleForm|FullyQualifiedName~PermissionGate|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~Backup"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный (`InvoiceRecognitionEndpoints.cs`, `InvoiceEndpoints.cs` под присмотром)
- [ ] 3.5 `bash scripts/backend-tests.sh` — весь набор двумя потоками зелёный
- [ ] 3.6 Оба PR с меткой `version:minor`; `<Version>` не тронута
