# Tasks: факты для индикатора состояния счёта

## 0. До кода

- [x] 0.1 Решение владельца **Э4** записано 07.11.2026 в `../costs-invoice-screen-00-epic/tasks.md` (0.1):
  «кто и когда изменил» нужен. [COST-6.4] Проверка — у Э4 стоят дата и выбор.
- [x] 0.2 Форма снимка — вариант А (`design.md` D4), выбор владельца 07.11.2026 вместе с Э4. [COST-6.4]
  Проверка — в D4 стоит выбор.

## 1. Срок в ответе счёта

- [x] 1.1 `InvoiceDueView` получает `DaysLeft` и `Overdue` из `InvoiceDue.DaysLeftOf` / `OverdueOf`;
  «сегодня» — `clock.TodayAsync` один раз на сборку вида (`InvoiceDesk`). [COST-9.1] Проверка —
  интеграционный тест: у трёх счетов (срок впереди, прошёл, оплачен) `due.daysLeft` и `due.overdue`
  равны полям их строк в `GET /api/costs/invoices`.
- [x] 1.2 Тип клиента `shared/api/invoices.ts`: необязательные `daysLeft`, `overdue` у `due`. [COST-9.1]
  Проверка — `npx tsc -b`.

## 2. Готовность: `null`/`[]`, «чему мешает», род отказа

- [x] 2.1 `InvoiceReadinessTests`: готовый черновик — `gaps == []`; разобранный, отклонённый и запертый —
  `gaps == null` (закрепить действующее). [COST-6.4] [COST-9] Проверка — тесты зелёные.
- [x] 2.2 `PaymentPosting.RefusalKind` (`amount` / `discrepancy` / `null`); `Refusal` строит текст по нему —
  правило одно. [COST-6.4] Проверка — `PaymentPostingTests`: род и текст на тех же случаях; прежние
  тексты не изменились.
- [ ] 2.3 `InvoiceReadinessGap.Blocks`: `parsed` у всех; `payment` у пункта, чинящего род отказа, только у
  неоплаченного (design D2). [COST-6.4] Проверка — тесты: расхождение → `total` с
  `["parsed","payment"]`; нет суммы → пункт `Итого` с обоими (после 01); строки без позиции → только
  `parsed`; оплаченный черновик → ни одного `payment`.
  ⏳ **Не отмечено до слияния 01**: расхождение, строки без позиции и оплаченный черновик проверены
  (`InvoiceStateFactsTests`); случай «нет суммы → пункт `Итого` с обоими» ждёт change
  `costs-invoice-screen-01-parsed-amount` — без него пункта поля «Итого» нет. Код уже ставит `payment`
  этому пункту по роду `amount` (`InvoiceReadiness.BlocksPayment`); после слияния 01 — дописать тест и
  отметить задачу.
- [x] 2.4 `PaymentView.RefusalKind` (`amount`, `discrepancy`, `rejected`). [COST-6.4] Проверка —
  интеграционный тест: на одном счёте `refusalKind`, пункт с `payment` в `blocks` и отказ `POST …/paid`
  согласованы; у разобранного без суммы `gaps == null`, а `refusalKind == amount`.
- [x] 2.5 Типы клиента: `blocks?` у пункта, `refusalKind?` у оплаты. [COST-6.4] Проверка — `npx tsc -b`;
  `npm test` зелёный (`invoiceReadiness.test.ts` не меняется).

## 3. Кто изменил счёт последним (Э4, вариант А)

- [x] 3.1 Вариант А: колонки `updated_by`, `updated_by_name`, `updated_via` у `Invoice`; миграция
  `dotnet ef migrations add InvoiceUpdatedBy --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext`;
  автор ставится при сохранении изменённого счёта (design D4), у `MergeAsync` — `recognition`, у прочих
  фоновых — `system`. [COST-6.4] Проверка — миграция применяется на пересозданной базе; `dotnet build`
  зелёный.
- [x] 3.2 `InvoiceView.UpdatedBy` (род, имя, `mine`); пусто, если автора нет. [COST-6.4] Проверка —
  интеграционный тест двумя пользователями: Б сохраняет шапку → у А `user`, имя Б, `mine: false`;
  распознавание (поддельный движок) → `recognition`; `POST …/draft` у черновика → версия и `updatedBy`
  прежние.
- [x] 3.3 Копия: счёт с автором возвращается из копии тем же (`InvoiceBackupRoundTripTests`); копия старше
  колонок восстанавливается с пустым автором (`ModuleBackupShapeTests` — форма уже покрыта, проверить
  прогоном); `BackupManifestCoverageTests` зелёный. [COST-6.4] Проверка — тесты зелёные.
- [x] 3.4 Тип клиента `updatedBy?`. [COST-6.4] Проверка — `npx tsc -b`.

## 4. Документы

- [x] 4.1 `docs/DEV_NOTES.md`, раздел о счёте: «чему мешает» берётся из правила оплаты; автор последней
  записи — снимок при сохранении. [COST-6.4] Проверка — ревью PR.

## 5. Верификация

- [x] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~InvoiceReadiness|FullyQualifiedName~PaymentPosting|FullyQualifiedName~InvoiceDue|FullyQualifiedName~InvoiceStaleForm|FullyQualifiedName~Backup"` — зелёный
- [x] 5.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [x] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный (`InvoiceContracts.cs`, `InvoiceReadiness.cs` под присмотром)
- [x] 5.5 `bash scripts/backend-tests.sh` — весь набор двумя потоками зелёный
- [ ] 5.6 PR с меткой `version:minor`; `<Version>` не тронута
