# Tasks: «Внести оплату» вместо «Отметить оплату»

Один PR с «Closes #1866». Поведение не меняется, поэтому метки `version:minor` не нужно (PATCH).
Макетов не нужно: меняется только подпись (design.md, Non-Goals).

## 0. До кода

- [ ] 0.1 Решения владельца записаны в design.md с датой: подписи (D1) и существительное в текстах (D2).
  Если выбран не рекомендованный вариант, delta-спецификация и tasks обновлены через `/opsx:update`.
  Проверка — дата и вариант в D1 и D2

## 1. Подписи

- [ ] 1.1 Клиент: «Внести оплату…» в сводке (`InvoiceSummary.tsx`) и в шапке части (`InvoicePayButton`);
  «Внести оплату» / «Внести оплату с переносом» в окне оплаты (`InvoicePayment.tsx`, `payLabel`);
  комментарии с цитатой подписи — туда же [COST-4] [COST-6.2]. Проверка — vitest:
  `paymentPosting.test.ts`, `invoicePrimary.test.ts`, `operationDialogs.test.tsx` с новыми подписями зелёные
- [ ] 1.2 Живые прогоны: `costs-smoke`, `invoice-payment-smoke`, `invoices-smoke`,
  `invoice-terms-collapse-checks` и `e2e/README.md` ищут кнопку по новой подписи (D4) [COST-4]. Проверка —
  эти прогоны зелёные на стенде; `grep -rn "Отметить оплату" src docs` пуст
- [ ] 1.3 Сервер (если так в D2): описание права `costs.invoice.pay` (`CostsModule.cs`) и роли «Бухгалтер»
  (`SystemRoles.cs`); выяснено, обновляется ли описание у существующей роли (D3). Проверка —
  `dotnet test … --filter "FullyQualifiedName~SystemRoles|FullyQualifiedName~Permission"` зелёный;
  ответ по D3 — в описании PR

## 2. Документы

- [ ] 2.1 `docs/USER_GUIDE.md` (оплата, сводка, «Сроки и оплата», отклонённый счёт) и `docs/ADMIN_GUIDE.md`
  (права бухгалтера) — новая подпись; снимки с видимой кнопкой пересняты. Проверка — `grep` по
  «Отметить оплату» пуст; оглавление перестроено `npm run toc`, если менялись главы

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FileSizeRatchet"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm test` — зелёный
- [ ] 3.4 `cd src/client && npm run ui:ratchet` — без роста
