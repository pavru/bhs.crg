# Tasks: межмодульный контракт и параметр доступа контракта

## 1. Объявление и регистрация

- [ ] 1.1 `BHS.CRG.Modules/Exchange/ModuleContractAttribute.cs` (поставщик, название, `Many`),
  `IModuleContracts.cs` (`Get<T>` → `Connected` | `NotConnected`, `GetAll<T>`), регистрация
  `AddModuleContract<T, Impl>(module)` [CORE-34] [OVW-12]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 `Api/Modules/ModuleContractCatalog.cs` — проверка при старте: одна реализация у включённого
  поставщика, регистрирует названный модуль, у `Many` — не больше одной на модуль [CORE-34]. Проверка —
  `ModuleContractCatalogTests` на пробных модулях: без реализации, две реализации, чужая регистрация —
  отказ старта с названиями; `Many` с двумя модулями — обе в `GetAll`
- [ ] 1.3 Перепись: прямой запрос интерфейса с `[ModuleContract]` из DI вне `IModuleContracts` — красный
  [CORE-34]. Проверка — образец прямого запроса в тесте сторожа находится

## 2. Параметр доступа и общие ответы

- [ ] 2.1 `BHS.CRG.Modules/Access/ModuleReader.cs` (`UserId?`, `Who`, `Keys`, `SystemReason`, `IsSystem`) и
  построение в `Api`: запрос — из `IUserPermissions`, фоновая операция — от имени поставившего, иначе
  система с причиной [CORE-34] [AUTH-6]. Проверка — `ModuleReaderTests`: три источника
- [ ] 2.2 `Exchange/ContractAnswer.cs`: `Hidden(permission)`, `SystemRefused(reason)` [CORE-34]. Проверка —
  `ModuleContractAnswerTests` пробного контракта: без права — `Hidden`, система — `SystemRefused`,
  выключенный поставщик — `NotConnected`
- [ ] 2.3 `docs/DEV_NOTES.md`: как завести контракт, почему поставщик назван у интерфейса, когда `Many`,
  что добавит `work-07` в `ModuleReader`. Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModuleContract|FullyQualifiedName~ModuleReader|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
