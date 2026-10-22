# Tasks: таблицы и опубликованные наборы модуля `plan`

## 1. Таблицы

- [ ] 1.1 `Modules.Plan/Tables/EstimateRowsTable.cs` и служба строк над методом «по действующим» `plan-06`;
  денежные колонки и поля `ПозицияСметы` — `Requires: "plan.estimate.cost"`; «Распределено» из `plan-10`
  [CORE-33] [PLAN-5.2] [PLAN-11]. Проверка — `PlanTablesTests`: повторный импорт — строки только
  действующей редакции; без `plan.estimate.cost` — денежных значений нет в строках и XLSX
- [ ] 1.2 `Tables/OfferTable.cs` (действующие версии, без себестоимости) и `Tables/ScheduleWorksTable.cs`
  с предупреждениями кодами [CORE-33] [PLAN-12] [PLAN-17]. Проверка — тот же тест: без `plan.offer.read` —
  403; в таблице КП нет колонок себестоимости
- [ ] 1.3 `PlanModule.Tables`, представления «Строки смет», «Реестр КП», «Работы графиков», тексты границ
  выдачи [CORE-24.3]. Проверка — старт с `plan` проходит проверку объявлений `ModuleTableCatalog`

## 2. Наборы и переписи

- [ ] 2.1 Наборы `system:table:plan.*` из таблиц [CORE-24] [STG-6.2]. Проверка — `PlanTablesTests`: набор
  строк смет под «Сметчиком» без себестоимости не несёт денег
- [ ] 2.2 `ModuleMoneyStorageTests`: модуль `plan` в переписи модулей с деньгами [STG-6] [PLAN-Q8].
  Проверка — перепись зелёная; денежный тэг на типе в общей таблице её роняет
- [ ] 2.3 Ссылки «Открыть таблицей» на экранах смет, КП и графиков [CORE-33]. Проверка — vitest на адрес
  ссылки
- [ ] 2.4 `docs/USER_GUIDE.md`: наборы `plan` и что скрыто правами; `cd docs/tools && npm run toc` [CORE-24].
  Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PlanTables|FullyQualifiedName~ModuleMoneyStorage|FullyQualifiedName~ReadAllWritesNothing|FullyQualifiedName~ModuleTable"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
