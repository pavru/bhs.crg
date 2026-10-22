# Tasks: периодический такт модульных заданий

## 1. Объявление такта

- [ ] 1.1 `BHS.CRG.Modules/Ports/ModuleTick.cs` (`Code`, `Title`, `Operation`, `Period`) и
  `IAppModule.Ticks => []` [WORK-41]. Проверка — `ModuleBoundaryTests` зелёный, модули собираются без правок
- [ ] 1.2 Проверка при старте: обработчик операции есть, код уникален в модуле, период не меньше минуты
  [WORK-41]. Проверка — `ModuleTickDeclarationTests`: три отказа старта с названием модуля и такта

## 2. Служба такта

- [ ] 2.1 `Api/Modules/ModuleTickService.cs` по образцу `BackupScheduleService`: шаг минута, пауза на
  старте, `TimeProvider`, только включённые модули [WORK-41] [WORK-10]. Проверка — `ModuleTickServiceTests`
  на пробном модуле: срок прошёл — одна задача в индикаторе; модуль выключен — ни одной
- [ ] 2.2 Постоянная цель такта (`модуль:код`), «когда ставили последнюю» — из таблицы задач; «уже
  выполняется» — штатный исход [WORK-41]. Проверка — тест: задача идёт — вторая не ставится, в журнале
  сервера ошибки нет; сдвиг часов на неделю — одна задача

## 3. От имени системы

- [ ] 3.1 `ModuleWorkRunner.cs`: у задачи такта автор журнала «Система — <название>», параметр доступа
  `DataAccess.OfSystem(<название>)` [WORK-41] [CORE-24.1]. Проверка — тест: запись журнала из задачи
  несёт это имя и не несёт учётной записи; чтение опубликованного набора — отказ с названием такта
- [ ] 3.2 `docs/DEV_NOTES.md`: такт против своего фонового цикла в модуле; догон — запросом модуля.
  Проверка — ревью PR

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModuleTick|FullyQualifiedName~ModuleWork|FullyQualifiedName~BackupSchedule|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
