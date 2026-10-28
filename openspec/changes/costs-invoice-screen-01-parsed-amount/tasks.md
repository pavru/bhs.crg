# Tasks: сумма к оплате обязательна для «Разобран»

## 0. До кода

- [ ] 0.1 Решение владельца **Э3** записано с датой в `../costs-invoice-screen-00-epic/tasks.md` (0.1) и
  перенесено сюда, в `design.md` D3. Варианты: «остаются разобранными» (рекомендация зонтика, миграции
  нет) или «возвращаются в черновик» (миграция данных и журнал, правка через `/opsx:update`). [COST-9]
  Проверка — у Э3 стоят дата и выбор; при втором варианте спецификация и задачи обновлены до кода.
- [ ] 0.2 Сколько счетов затронуто: на стенде с копией базы заказчика выполнить
  `select count(*) from costs.invoices where state = 'Parsed' and (total is null or total = 0)`
  (имена колонок сверить с `CostsDbContext`). Число записано в Э3 до решения. [COST-9] Проверка — число
  стоит рядом с Э3.

## 1. Обязательность суммы

- [ ] 1.1 `CostsRecordTypes.cs`: `Required: true` у «Итого». Комментарий объявления называет решение
  владельца 27.10.2026 (ответ 1 листа 0). [COST-9] Проверка — `ModuleRecordTypeTests` зелёный;
  `InvoiceReadiness.Required` содержит `Итого`.
- [ ] 1.2 Одно правило «суммы нет» (`Total is null or 0`) для `PaymentPosting.Refusal` и
  `InvoiceRequisites.MissingFields` (только ключ `TotalKey`) — design D2. [COST-9] Проверка — unit-тест
  в `InvoiceRequisitesTests`: `null`, `0`, `0.00` — пусто, `0.01` — заполнено; `PaymentPostingTests`
  зелёный без правок.
- [ ] 1.3 `InvoiceReadinessTests`: черновик без суммы — пункт `field` с `Field = "Итого"` среди полей
  шапки, до строк; `POST …/parsed` — 400 со словами «Сумма к оплате»; сумма `0` — то же самое. [COST-9]
  Проверка — новые тесты зелёные.
- [ ] 1.4 `InvoiceParsedStateTests`: у разобранного счёта сумму стирают правкой шапки — правка ложится,
  счёт в черновике, журнал называет «Сумма к оплате». Разобранный без суммы, заведённый в базе в обход
  перехода (Э3), читается разобранным, а `payment.refusal` называет сумму. [COST-9] Проверка — тесты
  зелёные.
- [ ] 1.5 Посев тестов: прогнать `FullyQualifiedName~Invoice`. Фикстуры, разбирающие счёт без суммы,
  получают сумму, равную сумме строк. Ни один тест не ослаблен ради зелёного. [COST-9] Проверка —
  прогон зелёный; в описании PR — список тронутых фикстур.
- [ ] 1.6 Клиент: в vitest-наборах, перечисляющих обязательные поля, — ключ `Итого`
  (`invoiceReadiness.test.ts` и соседи). Посев e2e (`e2e/seed-invoices.mjs`) разбирает счета только с
  суммой. [COST-9] Проверка — `npm test` зелёный; `invoices-smoke` и `invoice-lines-smoke` зелёные.

## 2. Документы

- [ ] 2.1 `docs/tz/55-счета-и-накладные.md` [COST-9]: строка «Решение 27.10.2026: для перехода
  «разобран» обязательна и сумма к оплате; нуль — не сумма; уже разобранные — по Э3». Проверка — ревью PR.
- [ ] 2.2 `docs/USER_GUIDE.md` 13.9: в перечне обязательных — сумма к оплате; фраза о разобранном без
  суммы («вернётся в черновик первой правкой шапки»). Если менялись заголовки — `cd docs/tools && npm run
  toc`. [COST-9] Проверка — ревью PR; проверка оглавления в «Frontend» зелёная.

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Invoice|FullyQualifiedName~PaymentPosting|FullyQualifiedName~ModuleRecordType"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста (клиент почти не меняется)
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 3.5 `bash scripts/backend-tests.sh` — весь набор двумя потоками зелёный, как в CI
- [ ] 3.6 PR с меткой `version:minor`; `<Version>` не тронута
