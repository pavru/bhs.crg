# Tasks: периодический такт модульных заданий

## 1. Объявление такта

- [x] 1.1 `BHS.CRG.Modules/Ports/ModuleTick.cs` (`Code`, `Title`, `Operation`, `Period`) и
  `IAppModule.Ticks => []` [WORK-41]. Проверка — `ModuleBoundaryTests` зелёный, модули собираются без правок
- [x] 1.2 Проверка при старте: код уникален в модуле, период не меньше минуты, операция своего модуля —
  каталог тактов; обработчик операции есть — `StartupTasks` [WORK-41]. Проверка —
  `ModuleTickDeclarationTests`: четыре отказа старта с названием модуля и такта

## 2. Служба такта

- [x] 2.1 `Api/Modules/ModuleTickService.cs` по образцу `BackupScheduleService`: шаг минута, пауза на
  старте, `TimeProvider`, только включённые модули [WORK-41] [WORK-10]. Проверка — `ModuleTickServiceTests`
  на пробном модуле: срок прошёл — одна задача в общей очереди; модуль выключен — ни одной
- [x] 2.2 Постоянная цель такта (`модуль:код`), «когда ставили последнюю» — из таблицы задач
  (`IJobService.GetLatestModuleWorkAsync`); «уже выполняется» — штатный исход [WORK-41]. Проверка — тест: задача идёт — вторая не ставится, в журнале
  сервера ошибки нет; сдвиг часов на неделю — одна задача

## 3. От имени системы

- [x] 3.1 `ModuleWorkRunner.cs`: у задачи такта автор журнала «Система — <название>»; параметр доступа —
  `IModuleReaders.ForJobAsync` отвечает `ModuleReader.OfSystem(<название>)` [WORK-41] [CORE-24.1].
  Проверка — тест: запись журнала из задачи несёт это имя и не несёт учётной записи; контракт отвечает
  задаче отказом с названием такта
- [x] 3.2 `docs/DEV_NOTES.md`: такт против своего фонового цикла в модуле; догон — запросом модуля.
  Проверка — ревью PR

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ModuleTick|FullyQualifiedName~ModuleWork|FullyQualifiedName~BackupSchedule|FullyQualifiedName~ModuleBoundary"` — зелёный
- [x] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
