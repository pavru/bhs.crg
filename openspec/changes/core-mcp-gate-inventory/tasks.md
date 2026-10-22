# Tasks: опись ворот MCP берётся из регистрации

PR без изменения поведения продукта — без метки `version:minor`, в описании `Closes #1337`.

## 1. Опись

- [ ] 1.1 `Tests/Mcp/McpInventory.cs`: имена примитивов из регистрации тестового хоста, методы по
  атрибутам SDK в сборках `BHS.CRG.*` (с модулями поставки, без тестовой), сопоставление по имени;
  способ чтения регистрации — в доккомментарий (D1) [AUTH-12.1]. Проверка — опись находит 26
  инструментов, 7 ресурсов, 2 промпта
- [ ] 1.2 Проверка расхождения в обе стороны и пустой описи (D2) [AUTH-12.1]. Проверка — тест
  `Опись_совпадает_с_регистрацией` зелёный; временное снятие одного `WithTools<…>` из регистрации даёт
  красный с именами его инструментов (проверено руками, результат — в описании PR)
- [ ] 1.3 Сторож проверен нарушением: класс `[McpServerToolType]` с инструментом без ворот в тестовой
  сборке, нигде не вписанный; тест подаёт описи тестовую сборку и ждёт отказа с именем инструмента (D4)
  [AUTH-12.1]. Проверка — тест зелёный, основная опись этот класс не видит

## 2. Переход тестов на опись

- [ ] 2.1 `McpGateInventoryTests` — без `ToolTypes` / `ResourceTypes` / `PromptTypes` (D5) [AUTH-12.1].
  Проверка — все тесты класса зелёные
- [ ] 2.2 `McpToolContractTests` — без `ToolTypes`; `WriteTools` и `DestructiveTools` остаются руками
  и сверяются с описью (D5) [AUTH-12.1]. Проверка — тесты класса зелёные
- [ ] 2.3 `McpToolGateTests.AllDeclaredTools` — из описи (D5) [AUTH-12.1]. Проверка — тесты класса
  зелёные; grep `typeof(DataSnapshotTools)` в `BHS.CRG.Tests` пуст

## 3. Комментарии

- [ ] 3.1 `ServiceRegistration.Domain.cs:138` и `McpGates.cs:10` — по D6. Проверка — ревью PR;
  grep «Только чтение: инструментов записи» и «двадцать семь» в `src/server` пуст

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~McpGateInventory|FullyQualifiedName~McpToolContract|FullyQualifiedName~McpToolGate|FullyQualifiedName~McpInventory"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
