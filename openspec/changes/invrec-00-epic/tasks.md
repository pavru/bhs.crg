# Tasks: распознавание счёта по профилю — перечень дочерних change

Галка ставится, когда все PR дочернего change слиты и change заархивирован. Архитектура — `design.md`.

## 0. Сделано до зонтика

- [x] 0.1 #1480, #1481, #1482, #1483 слиты (`design.md`, D2). Проверка — коммиты `55d47eff`, `4f1dbe9a`,
  `377bae09`, `dfd58540` в master

## 1. Оставшийся ребёнок

- [ ] 1.1 `invrec-05-unplaced` (2) — «прочитано, в счёт не положено» с причиной, хранится всегда, #1484
  [COST-8]. Проверка — change заархивирован
- [ ] 1.2 Закрыть #1479, когда закрыт #1484. Проверка — issue закрыт с перечнем PR

## 2. Верификация эпика

- [ ] 2.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~InvoiceScan|FullyQualifiedName~InvoiceFromScan"` — зелёный
- [ ] 2.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 2.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 2.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный

## Workflow follow-up

- Зонтик архивируется после `invrec-05-unplaced`.
