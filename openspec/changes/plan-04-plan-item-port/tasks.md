# Tasks: порт «найди или создай» позиции перечня

## 1. Порт

- [ ] 1.1 `BHS.CRG.Modules/Ports/IModulePlanItems.cs`: `PlanItemKey`, `FindOrCreateAsync`,
  `FindOrCreateManyAsync`, `GetAsync`; результат с признаком `Created` и отказом по составляющей
  [CORE-12]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 `Api/Modules/Ports/ModulePlanItemsPort.cs`: вставка с `ON CONFLICT DO NOTHING` по уникальному
  ключу и чтение по ключу одним запросом на пакет; регистрация в `ModulePorts.cs` [CORE-12] [STG-6.1].
  Проверка — `ModulePlanItemsPortTests`: два параллельных вызова — одна позиция; повтор — `Created: false`
- [ ] 1.3 Проверки ключа: вид работы и единица — не архивные записи своих типов, раздел принадлежит
  стройке; отказы поимённо, пакет не отвергается целиком [CORE-12] [CORE-34.4]. Проверка — тесты: архивный
  вид, раздел чужой стройки, пакет 300 с одним плохим
- [ ] 1.4 Пустой раздел — одна позиция «стройка в целом» [CORE-10] [CORE-Q2]. Проверка — тест: два вызова
  без раздела — одна позиция

## 2. Документы и комментарии

- [ ] 2.1 Переписать комментарии `WorkPlanItem.cs:28-32` и `WorkPlanItems.cs` — правило теперь есть, зовёт
  его порт [CORE-12]. Проверка — ревью PR
- [ ] 2.2 `docs/DEV_NOTES.md`: как модуль заводит позицию, почему до своих строк и почему висячие позиции
  допустимы [CORE-4] [STG-6.1]. Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModulePlanItemsPort|FullyQualifiedName~WorkPlanItem|FullyQualifiedName~ModuleBoundary|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
