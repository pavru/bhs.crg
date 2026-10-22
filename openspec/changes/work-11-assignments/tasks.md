# Tasks: назначения монтажника и менеджера

## 1. Порты ядра

- [ ] 1.1 `ModuleConstruction` получает `TimeZoneId` (действующий) и `TimeZoneInherited`; заполнение в
  `ModuleConstructionsPort.cs` с поясом компании из `IAppSettingsStore` [CORE-5] [WORK-8]. Проверка —
  тест порта: стройка без пояса приходит с поясом компании и признаком; тесты `costs` зелёные
- [ ] 1.2 `IModuleClock.TodayOnSiteAsync` и `SiteDateOf` в `ModuleClockPort.cs`; двойник
  `InvoiceDueTableTests.TestClock` дополнен [CORE-5] [WORK-8]. Проверка — тест на подставном
  `TimeProvider`: 23:59:59 и 00:00:00 по поясу стройки дают разные даты; Владивосток против Москвы;
  удалённая стройка — `null`
- [ ] 1.3 `IModuleEmployees` и `ModuleEmployeesPort`: поля по тэгам `employee.*`, архив, состояние связи из
  таблицы `work-01`; справочник не заведён — `null` [CORE-7] [CORE-20.4]. Проверка — тесты: чистая база
  без «Персоны»; переименованная подпись «Уволен с»; удалённая учётная запись даёт `deleted`

## 2. Назначения в модуле

- [ ] 2.1 `Data/Assignment.cs`, `WorkDbContext` и миграция
  `dotnet ef migrations add Assignments --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`;
  проверка в базе «конец null только у `manager`» [WORK-21] [WORK-46]. Проверка — на пересозданной базе
  миграция применяется; вставка `installer` без конца отвергается базой
- [ ] 2.2 `Access/AssignmentGate.cs` — право по стройке (`work.assign` + действующее назначение «менеджер»
  или `work.sites.all`) [WORK-21] [WORK-46]. Проверка — тест: чужая стройка — 403; с `work.sites.all` —
  успех; учётная запись без сотрудника — 403 с причиной
- [ ] 2.3 Адреса `GET/POST /api/work/assignments`, `PUT/DELETE /api/work/assignments/{id}` с `If-Match`
  через `SeenVersion`, отказ сменить сотрудника, стройку или вид [WORK-21] [AUTH-8] [AUTH-9]. Проверка —
  тесты: без `If-Match` — 400, устаревшая версия — 409, монтажник без `work.assign` — 403, хост без `work`
  — 501; `EndpointGateInventoryTests` зелёный
- [ ] 2.4 Признаки `canAssign`, `canChange`, `canRemove` с причиной в ответе чтения назначений — от
  `AssignmentGate` [WORK-21] [WORK-46]. Проверка — тест: для каждого из трёх случаев (своя стройка, чужая,
  последний менеджер) признак совпадает с ответом пишущего адреса
- [ ] 2.5 Проверки назначения: пересечение на той же стройке того же вида — 409 с периодом; сотрудник
  уволен, не принят или в архиве на часть периода — 422 с причиной; `manager` без учётной записи — 422;
  последнее назначение «менеджер» снимает только `work.sites.all` [WORK-21] [WORK-27] [WORK-46].
  Проверка — по тесту на каждый отказ; две стройки в один день — успех
- [ ] 2.6 `GET /api/work/assignments/candidates` (`eligible`, `reason`) и `POST /api/work/assignments/bulk`
  «всё или ничего» [WORK-21]. Проверка — тест: пачка с одним уволенным даёт 422 с его именем, ни одного
  назначения не заведено
- [ ] 2.7 Объявления ссылок в `WorkReferences.cs` на сотрудника и стройку [CORE-34.1]. Проверка — тест:
  удаление сотрудника с тремя назначениями — отказ «назначения: 3»; `ModuleReferenceInventoryTests` зелёный
- [ ] 2.8 Действия `work.assignment.*` в журнал ядра через `IModuleActivityLog` [CORE-28]. Проверка —
  тест: снятие оставляет запись с прежним периодом; `ActivityLogInventoryTests` зелёный

## 3. Свои стройки и разрешение доступа

- [ ] 3.1 `GET /api/work/sites/mine?date=` — назначенные стройки с `today` и `boundary` («ваши стройки
  (N)», `reason: not-assigned`) [WORK-24] [WORK-8] [CORE-24.3]. Проверка — тест: из 40 строек приходят
  две назначенные; без назначений — `not-assigned`; Владивосток в 20:00 по Москве даёт `today` = завтра
- [ ] 3.2 `Access/ManagerSites.cs` наполняет параметр доступа `work-07` действующими назначениями
  «менеджер» [WORK-46] [WORK-39] [WORK-40]. Проверка — тест: после снятия назначения следующий запрос
  стройку не видит; открытый конец действует на любую дату после начала
- [ ] 3.3 `docs/ADMIN_GUIDE.md`: кто назначает, что даёт `work.sites.all`, почему менеджер должен быть
  связан с учётной записью; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend» (содержание)
  зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Assignment|FullyQualifiedName~ModuleClock|FullyQualifiedName~ModuleEmployees|FullyQualifiedName~ModuleConstructions|FullyQualifiedName~ActivityLogInventory|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~Costs"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
