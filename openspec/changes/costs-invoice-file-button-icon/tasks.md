# Tasks: значок «Файл» показывает, что сделает нажатие

Один клиентский PR с «Closes #1874». Исправление вида — PATCH, метки `version:minor` не нужно. Макетов не
нужно (proposal, Impact).

## 0. До кода

- [ ] 0.1 Решение владельца по D1–D3 записано в design.md с датой; если выбрано не рекомендованное —
  delta-спецификация и tasks обновлены через `/opsx:update`. Проверка — дата и вариант в D1–D3

## 1. Значок

- [ ] 1.1 `InvoiceFileButton`: значок по `file` — `closed` → `RightPanelOpen`, `beside` → `RightPanelClose`,
  `over` → `OpenInFull`; подпись, подсказки и `aria-pressed` прежние [COST-6.3]. Проверка —
  `InvoiceFileButton.test.tsx`: значок в каждом из трёх мест, имя «Файл» и `aria-pressed` в каждом
- [ ] 1.2 Проверка глазами на 1920 (список ↔ файл рядом) и на 1024 (поверх формы). Проверка — снимки трёх
  состояний сводки в описании PR

## 2. Документы

- [ ] 2.1 `docs/USER_GUIDE.md` — если описание сводки называет вид значка «Файл», поправить. Проверка —
  `grep` по «Файл» в разделе о сводке счёта

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FileSizeRatchet"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm test` — зелёный
- [ ] 3.4 `cd src/client && npm run ui:ratchet` — без роста
