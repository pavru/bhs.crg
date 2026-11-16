# Tasks: приёмка отчётов и очередь с пометками

## 1. PR 1: действия приёмки

- [x] 1.1 `POST /api/work/reports/{id}/accept` и `…/return` под `perm:work.report.review`, чтение отчёта
  с параметром доступа; возврат требует комментарий; виды протокола `Accepted`, `Returned` и журнала
  `accepted`, `returned` с колонкой `comment` [WORK-13] [WORK-46]. Проверка — `Integration/ReviewActionsTests`:
  «подан → принят», «подан → возвращён → подан», 400 без комментария, 409 `report-status` на черновике
- [x] 1.2 Проверка `If-Match` у приёмки, возврата и правки менеджером; ворота модуля и права [AUTH-8]
  [AUTH-9]. Проверка — `Integration/ReviewGateTests`: без заголовка 400, устаревшая версия 409, монтажник и
  руководитель 403, менеджер чужой стройки 404, выключенный модуль 501; `EndpointGateInventoryTests` зелёный
- [x] 1.3 Правка строк менеджером адресом `PUT /api/work/entry/reports/{id}`: запись журнала вида
  `manager-edited` с прежним значением одним `SaveChanges`, дубль в журнал ядра [WORK-14] [WORK-51].
  Пометку `manager-edit` из этой записи считает каталог PR 2. Проверка — тест: монтажник видит запись с
  именем менеджера; `ActivityLogInventoryTests` зелёный
- [x] 1.4 Носитель `acceptWouldMark` в ответе отчёта; запись «ввёл и принял один человек» при такой
  приёмке [WORK-31.1]. Проверка — тест: ввёл менеджер А — у А `true`, у Б `false`; после приёмки А
  в журнале запись `same-person`
- [x] 1.5 Личное уведомление о возврате учётной записи сотрудника, без ссылки до экрана; без связи —
  запись журнала «уведомить некого» [WORK-13] [WORK-56]. Проверка — тест: колокольчик монтажника
  получает, соседний — нет; `NotificationAudienceInventoryTests` зелёный
- [x] 1.6 Сводка о поданных за такт автоподачи: личная каждому менеджеру стройки с действующим
  назначением, отметка в таблице `work.review_digests`, миграция схемы `work` [WORK-56] [WORK-41]. Проверка —
  `Integration/ReviewDigestTests`: две стройки — две разные сводки; держатель `work.sites.all` без назначения
  не получает; два прогона такта — одна сводка; версия отчёта отметкой не сдвигается
- [x] 1.7 Правка возвращённого не помечается «правкой после подачи» [WORK-11] [WORK-13]. Проверка — тест:
  возврат, правка монтажника — `afterSubmission: false`; после повторной подачи правка — `true`

## 2. PR 2: очередь приёмки и каталог пометок

- [ ] 2.1 Каталог пометок `Review/ReviewMarks.cs` с кодами `edited-after-submit`, `same-person`,
  `manager-edit`, `site-not-assigned`, `photos-pending`, `late-delivery` и источником каждого [WORK-28]
  [MOB-21] [WORK-34]. Проверка — юнит-тест: у каждого кода текст и источник
- [ ] 2.2 `GET /api/work/review/queue`: отбор по стройке, датам, сотруднику и коду; `marks` и `actions`
  у строки, `marks` и в ответе чтения отчёта; `counts` по всем кодам по всему отбору [WORK-28] [WORK-39].
  Проверка — `Integration/ReviewQueueTests`: счётчик 70 при странице 50, руководитель без `actions`,
  монтажник 403, отчёт чужой стройки отсутствует
- [ ] 2.3 Граница выдачи `boundary` в ответе очереди (вид `work-07`) и пустая очередь с `reason`:
  `not-assigned`, `nothing-pending`, `filtered-out` [WORK-28] [WORK-46] [CORE-24.3]. Проверка — тест на
  каждую причину; «отчёты ваших строек (4)» не меняется от отбора по стройке, у руководителя — «все стройки»
- [ ] 2.4 Типы ответа и `WORK_MARK_CODES` в `src/client/src/shared/api/work.ts`; серверная сверка
  каталогов [WORK-28]. Проверка — `Work/ReviewMarkCatalogTests` падает на лишнем коде с любой стороны и
  на пустом списке клиента
- [ ] 2.5 Описать адреса очереди и приёмки в листинге REST API `docs/DEV_NOTES.md`. Проверка —
  маршруты в листинге совпадают с `Endpoints` модуля

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~.Review|FullyQualifiedName~NotificationAudience|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ActivityLogInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
