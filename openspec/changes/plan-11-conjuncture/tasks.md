# Tasks: конъюнктурный анализ

## 1. Сервер

- [ ] 1.1 `Data/ConjunctureAnalysis.cs`, `ConjunctureItem`, `ConjunctureOffer`, миграция `Conjuncture`
  (`--context PlanDbContext`); файл — `IModuleBlobs`, колонка в `IAppModule.Files` [PLAN-9]. Проверка —
  `ModuleFileInventoryTests` зелёный; на пересозданной базе старт применяет миграцию
- [ ] 1.1а `Data/VolumeStatement.cs` — ВОР приложением к смете тем же путём (ждёт решения В-7) [PLAN-7].
  Проверка — `VolumeStatementTests`: образец ВОР принят, план стройки не изменился
- [ ] 1.2 Загрузка: разбор `plan-05` фоновой операцией, отчёт, `accept` создаёт запись [PLAN-9] [PLAN-7].
  Проверка — `ConjunctureImportTests`: образец КА (из 0.2 зонтика) даёт позиции, предложения и выбранные
  цены; без `accept` записи нет
- [ ] 1.3 Чтение: без `plan.estimate.cost` — без цен и с `hidden: ["cost"]` [PLAN-9] [PLAN-2]. Проверка —
  тест: в ответе нет ни одного денежного поля
- [ ] 1.4 Удаление с `If-Match`; загрузка и удаление под `plan.estimate.edit` [AUTH-8] [AUTH-9]. Проверка —
  тесты отказов: без `If-Match` — 400, устаревший — 409, только `plan.estimate.read` — 403, модуль
  выключен — 501; `EndpointGateInventoryTests` зелёный

## 2. Экран

- [ ] 2.1 Сверить, что макет П1 (`plan-09`) покрывает раздел анализа; если нет — вопрос владельцу до кода
  [PLAN-9]. Проверка — ссылка на вариант макета в `design.md`
- [ ] 2.2 `shared/api/planningConjuncture.ts`, `features/planning/estimates/ConjunctureSection.tsx` на
  `DataGrid`: позиции, предложения, выбранная цена, «скрыто правами» [PLAN-9]. Проверка — vitest:
  `hidden: ["cost"]` — колонок цены нет, строка о скрытом есть
- [ ] 2.3 `docs/USER_GUIDE.md`: конъюнктурный анализ в смете; `npm run toc` в `docs/tools`. Проверка —
  проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Conjuncture|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleFileInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
