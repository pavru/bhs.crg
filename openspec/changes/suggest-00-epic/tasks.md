# Tasks: подсказки по ранее введённому — перечень дочерних change и PR

Каждая строка — дочерний change в `openspec/changes/suggest-NN-*`; галка ставится, когда его PR слит и
change заархивирован. Архитектура — `design.md`. Порядок — D12: **`suggest-01` → `suggest-02` →
`suggest-05`**; `suggest-03` параллельно с `suggest-02`, до `suggest-05`; `suggest-04` — потом и
необязательно. PR функциональных задач — с меткой **`version:minor`**; `<Version>` руками не правится.
Число в скобках — PR.

## 0. До начала

- [x] 0.1 Решения владельца и Архитектора по В-1…В-4 (22.10.2026: приняты рекомендации (а)) (`design.md`, Open Questions) — до кода
  `suggest-01`, `suggest-02`, `suggest-05`; записаны в `design.md` затронутых change. Проверка —
  `grep -rn "Рекомендация" openspec/changes/suggest-*/design.md` показывает только строки с пометкой
  «Решено»
- [x] 0.2 Замер повторяемости на копии базы заказчика (#1528, не PR) — итог в тексте эпика и в D2, D12

## 1. Ядро

- [x] 1.1 `suggest-01-rule` (1) — правило `Suggestable` (C#/TS, общий файл случаев), тэг
  `input.noSuggest`; #1529 [TYPE-2] [TYPE-21] [TYPE-22]. Метка `version:minor`. Проверка — change
  заархивирован
- [x] 1.2 `suggest-02-endpoint` (1) — адрес `GET /api/field-suggestions`, источник верхнего уровня,
  индекс, окно, права, 503; #1530 [CORE-34.4] [AUTH-8] [AUTH-9]. Условие — 1.1 и 0.1 (В-1…В-3). Метка
  `version:minor`. Проверка — change заархивирован, замер p95 в PR

## 2. Интерфейс

- [x] 2.1 `suggest-03-field` (1) — компонент `SuggestField`, стрелки `EditGrid`, правило в `docs/UI.md`;
  #1531 [UI-1]…[UI-8]. Параллельно с 1.2; первая задача change — три макета и выбор владельца. Метка
  `version:minor`. Проверка — change заархивирован
- [x] 2.2 `suggest-05-wiring` (1595) — подключение в шаг «Новая позиция», формы, ячейки, флажок в редакторе
  типа, e2e; #1533 [TYPE-2] [CORE-8] [CORE-9]. Условие — 1.2 и 2.1. Метка `version:minor`. Проверка —
  change заархивирован, e2e зелёные в CI

## 3. Потом и необязательно

- [x] 3.1 `suggest-04-nested` (1591) — источник вложенных полей и строк массивов по путям из схемы; #1532,
  **приоритет понижен**. Брать, когда в документах появятся набираемые таблицы с повторами или владелец
  попросит. Метка `version:minor`. Проверка — change заархивирован, замер p95 ≤ 150 мс в PR
  — **взята в работу 23.10.2026 по слову владельца** (`/opsx:apply suggest-04-nested`)

## 4. Верификация эпика

- [ ] 4.1 Профильные тесты: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~FieldSuggestion|FullyQualifiedName~Suggestable|FullyQualifiedName~ArchiveRead|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~AgentRulesCopy"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 4.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 4.4 Лимит 500 строк кода: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 «Не входит» соблюдено: `InvoiceExtraFields` адрес не зовёт, `text` и `primitive` не
  подсказываются. Проверка — `grep -rn "field-suggestions" src/client/src/features/costs` находит только
  шаг «Новая позиция»

## Workflow follow-up

- Issues по дочерним change не заводятся; PR закрывают свой issue строкой «Closes #N».
- Эпик #1527 закрывается после `suggest-05`; `suggest-04` (#1532) может остаться открытым отдельно.
- Зонтик архивируется последним, после всех взятых в работу дочерних change.
