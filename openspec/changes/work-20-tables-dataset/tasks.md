# Tasks: таблицы и системные наборы модуля `work`

## 1. Таблицы

- [ ] 1.1 Объявления `work.reports` (`RecordType: ОтчётМонтажника`, `Breakdown` — строки) и `work.lines`:
  `Requires: "work"`, `Isolation: PerUser`, колонки; `WorkModule.Tables` [CORE-33] [WORK-7]. Проверка —
  `SystemDataSetDeclarationTests` зелёный; в `work.lines` нет колонки часов
- [ ] 1.2 Службы строк обеих таблиц: отбор и итоги запросом с параметром доступа; индексы
  `(construction_id, date)` и `(employee_id, date)` миграцией схемы `work` [WORK-39] [WORK-40].
  Проверка — `Work/WorkTableIsolationTests`: монтажник А не видит отчёт Б ни в строках, ни в итогах, ни
  в числе строк; ключ строки чужого отчёта — 404; выгрузка менеджера стройки А — только стройка А
- [ ] 1.3 Граница выдачи полем ответа: «ваши отчёты», «отчёты ваших строек (N)», «все отчёты»,
  `reason: not-assigned` [CORE-24.3] [WORK-46]. Проверка — тест на менеджера с четырьмя стройками и без
  назначений
- [ ] 1.4 Чужой черновик — статус и момент автоподачи, колонки содержимого `null`; строк черновика в
  `work.lines` нет до подачи [WORK-54]. Проверка — `Work/WorkTableDraftTests`: до подачи и после; свой
  черновик виден целиком

## 2. Наборы и MCP

- [ ] 2.1 Наборы `system:table:work.reports` и `system:table:work.lines` из таблиц; параметр доступа
  читающего доходит до `ModuleTableDataProvider` [CORE-24] [STG-6.2] [AUTH-12.1]. Проверка — тест:
  снимок MCP с токеном монтажника А — только его отчёты
- [ ] 2.2 Набор `work.*` к печатной форме не привязывается [CORE-24.1]. Проверка — случай `work.lines`
  в `SystemDataSetDeclarationTests` (рядом с `:273`): отказ называет «печатная форма»
- [ ] 2.3 Право контракта и выключенный модуль [AUTH-8] [AUTH-17]. Проверка — тест: пользователь только
  с `work.facts.read` не получает таблиц и наборов `work`; без модуля наборов нет, `/api/tables/work.reports`
  — отказ выключенного модуля

## 3. Ссылка в карточку и документы

- [ ] 3.1 `RecordRoutesTests` — каталог с `WorkModule`; запись `ОтчётМонтажника` в `RECORD_ROUTES` —
  если карточка `work-21` уже слита, иначе её вносит `work-21` [CORE-33]. Проверка — `RecordRoutesTests`
  зелёный; `recordRoutes.test.ts` зелёный
- [ ] 3.2 `docs/USER_GUIDE.md` — таблицы «Учёта работ»: что видит монтажник, менеджер и руководитель;
  черновики наличием; содержание — `cd docs/tools && npm run toc` [WORK-39] [WORK-54]. Проверка —
  проверка содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Work.WorkTable|FullyQualifiedName~SystemDataSetDeclaration|FullyQualifiedName~RecordRoutes|FullyQualifiedName~ModuleTable"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
