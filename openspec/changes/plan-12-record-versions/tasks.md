# Tasks: базовый тип записи версии

## 1. Тип

- [ ] 1.1 `src/server/BHS.CRG.Modules/Data/ModuleRecordVersion.cs` — абстрактный `IAppendOnly`: `Id`,
  `RecordId`, `Number`, `Content` (jsonb), `Reason`, `AuthorId`, `At`; помощник конфигурации с единственным
  индексом `(RecordId, Number)` [CORE-36]. Проверка — `ModuleBoundaryTests` зелёный; тест на пробном
  контексте: правка версии отвергнута, второй номер — отвергнут индексом

## 2. Перепись

- [ ] 2.1 `src/server/BHS.CRG.Tests/Configuration/RecordVersionInventoryTests.cs` по образцу
  `InvoiceBookInventoryTests`: наследники во всех модулях (`SolutionModules`), словарь разрешений с
  причинами, запреты `ExecuteUpdate`/`ExecuteDelete`/сырого SQL, запрет обращения из файлов итогов [CORE-36].
  Проверка — тест зелёный на master (наследников ещё нет)
- [ ] 2.2 `InvoiceVersion` — исключение с причиной «живые данные не переносятся ради имени»; форма версии
  мимо базового типа требует записи в исключения [CORE-36]. Проверка — тест: пробный класс с формой версии
  без базового типа — красный
- [ ] 2.3 Самопроверка на образцах нарушений в тестовом каталоге — каждый запрет красный на своём образце
  [CORE-36]. Проверка — тест самопроверки зелёный, удаление любого запрета его роняет
- [ ] 2.4 `docs/DEV_NOTES.md`: как завести версии записи модуля, почему действующая — в основной таблице и
  почему итоги не читают историю. Проверка — ревью PR

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RecordVersionInventory|FullyQualifiedName~InvoiceBookInventory|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
