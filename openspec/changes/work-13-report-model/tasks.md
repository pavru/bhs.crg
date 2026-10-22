# Tasks: отчёт монтажника — модель, строки и протокол на будущее

## 1. PR 1: модель

- [ ] 1.1 Условие старта: сверить, что этап 3 завёл порт «найди или создай» позицию перечня [CORE-12].
  Проверка — порт есть в `src/server/BHS.CRG.Modules/Ports/`. Нет — дополнить `design.md` и `tasks.md`
  группой «PR ядра: порт позиций» с дельтой `core` и только потом писать код
- [ ] 1.2 `Data/Report.cs`, `ReportLine.cs`, `ReportLineMaterial.cs`, `ReportLineParticipant.cs`,
  `ReportChange.cs` (`IAppendOnly`); уникальный индекс четвёрки; миграция
  `dotnet ef migrations add Reports --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`
  [WORK-4] [WORK-5] [WORK-50] [MOB-35] [WORK-58]. Проверка — на пересозданной базе миграция
  применяется; тест: дневной и ночной отчёт одной даты сосуществуют, второй дневной — нарушение индекса
- [ ] 1.3 `Data/ReportRules.cs`: вариант своего вида, раздел своей стройки, материал только по
  номенклатуре, участники только у бригадной строки, `hours` от 0 до 24, часы в строке не принимаются
  [WORK-5] [WORK-7] [WORK-25] [WORK-49] [CORE-9]. Проверка — модульные тесты на каждое правило
- [ ] 1.4 `Data/WorkPlanItemLink.cs` — позиция через порт этапа 3: сначала ядро, потом строка; подсказка
  клиента не используется как ключ [CORE-12] [MOB-37] [CORE-4]. Проверка — интеграционные тесты: две
  параллельные строки одного ключа дают одну позицию; сбой сохранения строки и повтор не заводят второй
- [ ] 1.5 `WorkRecordTypes.cs`: `ОтчётМонтажника` (`Closed`) и `СтрокаРаботы.Сведения` (`Extendable`) с
  системными полями [CORE-19.2] [CORE-20.2] [TYPE-10] [TYPE-11]. Проверка — `SchemaEditLevelTests` на
  живом типе: поле в закрытом — отказ, необязательное в сведениях — успех, обязательное — отказ;
  `ModuleTypeProjectionTests` зелёный
- [ ] 1.6 `WorkReferences.cs`: все колонки-ссылки отчёта и строки, `details` — держащий JSON [CORE-34.1].
  Проверка — `ModuleReferenceInventoryTests` зелёный; тест: удаление занятого вида работы — отказ
  «строки отчётов монтажников: N»
- [ ] 1.7 Проверка новой ссылки на архивную запись через `IModuleReferenceTargets.StatesAsync` [CORE-34.4].
  Проверка — тесты: новый архивный материал — 400 с именем поля; правка объёма при архивном материале
  принимается

## 2. PR 2: адреса и протокол

- [ ] 2.1 `Endpoints/ReportEndpoints.cs` под `work.report.own` за воротами модуля; сотрудник из связи
  учётной записи [WORK-39] [AUTH-8] [AUTH-9]. Проверка — тесты: `work.report.read.all` без `own` — 403;
  хост без `work` — 501; учётная запись без сотрудника — 403 с причиной; сотрудник в теле — 400;
  `EndpointGateInventoryTests` зелёный
- [ ] 2.2 `ReportAccess.FindOwnAsync` — чтение через параметр доступа `work-07`, чужой и несуществующий —
  один 404 [WORK-39] [WORK-40]. Проверка — тест: `GET`, `PUT`, `DELETE` чужого отчёта — 404, отчёт Б не
  изменён; перепись `work-07` зелёная
- [ ] 2.3 `POST /api/work/reports` с клиентским `id`: повтор — 200 `replayed: true`; чужой `id` — 409 без
  полей [MOB-15]. Проверка — тесты повтора и чужого идентификатора
- [ ] 2.4 `PUT /api/work/reports/{id}` с `If-Match` и `changeId`, `DELETE` только черновика [MOB-15]
  [AUTH-8]. Проверка — тесты: без `If-Match` — 400; устаревшая версия с новым `changeId` — 409; повтор
  применённого `changeId` — 200 без второго применения; удаление не черновика — 409
- [ ] 2.5 Серверная последовательность и `GET /api/work/reports?since=` с удалёнными и курсором [MOB-16].
  Проверка — тест: правка с `deviceTime` в прошлом получает больший номер и приходит в `since`; удалённый
  черновик — в списке удалённых
- [ ] 2.6 `deviceTime` и время сервера в `report_changes`, без отказа при расхождении [MOB-17]. Проверка —
  тест: `deviceTime` на час вперёд принят, оба времени сохранены
- [ ] 2.7 `assignedOnDate` в ответе чтения по назначениям `work-11` [MOB-21]. Проверка — тест: назначение
  кончилось вчера — отчёт сохранён, `assignedOnDate: false`
- [ ] 2.8 `Endpoints/EntryReportEndpoints.cs`: `POST`, `GET`, `PUT /api/work/entry/reports` под
  `work.report.review` с параметром доступа «стройки», сотрудник из тела, `enteredBy` в ответе, колонка
  `entered_by_user_id` [WORK-31] [WORK-2] [AUTH-8] [AUTH-9]. Проверка — тесты: отчёт принадлежит
  сотруднику, `enteredBy` — менеджер; чужая стройка на чтении, правке и заведении — 404; без `If-Match` —
  400; устаревшая версия — 409; `work.report.own` без `review` — 403; хост без `work` — 501;
  `EndpointGateInventoryTests` зелёный
- [ ] 2.9 `docs/DEV_NOTES.md`: протокол повтора (`id`, `changeId`), курсор `since`, `deviceTime` — для
  этапа 5. Проверка — раздел есть, ссылки на `MOB-15`–`MOB-17`

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Work|FullyQualifiedName~SchemaEditLevel|FullyQualifiedName~ModuleTypeProjection|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~RecordWriteGuardCoverage|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~WorkPlanItem"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
