# Tasks: приёмка отчётов и очередь с пометками

## 1. PR 1: действия приёмки

- [ ] 1.1 `POST /api/work/reports/{id}/accept` и `…/return` под `perm:work.report.review`, чтение отчёта
  с параметром доступа; возврат требует комментарий [WORK-13] [WORK-46]. Проверка — `Work/ReviewActionsTests`:
  «подан → принят», «подан → возвращён → подан», 400 без комментария, 409 со статусом на черновике
- [ ] 1.2 Проверка `If-Match` у приёмки, возврата и правки менеджером; ворота модуля и права [AUTH-8]
  [AUTH-9]. Проверка — `Work/ReviewGateTests`: без заголовка 400, устаревшая версия 409, монтажник и
  руководитель 403, менеджер чужой стройки 404, выключенный модуль 501; `EndpointGateInventoryTests` зелёный
- [ ] 1.3 Правка строк менеджером: запись журнала с прежним значением одним `SaveChanges`, пометка
  `manager-edit`, дубль в журнал ядра [WORK-14] [WORK-51]. Проверка — тест: монтажник видит запись с
  именем менеджера; `ActivityLogInventoryTests` зелёный
- [ ] 1.4 Носитель `acceptWouldMark` в ответе отчёта; запись «ввёл и принял один человек» при такой
  приёмке [WORK-31.1]. Проверка — тест: ввёл менеджер А — у А `true`, у Б `false`; после приёмки А
  пометка `same-person` остаётся
- [ ] 1.5 Личное уведомление о возврате учётной записи сотрудника; без связи — запись журнала «уведомить
  некого» [WORK-13] [WORK-56]. Проверка — тест: колокольчик монтажника получает, соседний — нет;
  `NotificationAudienceInventoryTests` зелёный
- [ ] 1.6 Сводка о поданных за такт автоподачи: личная каждому менеджеру стройки с действующим
  назначением, отметка `review_digest_at`, миграция схемы `work` [WORK-56] [WORK-41]. Проверка —
  `Work/ReviewDigestTests`: две стройки — две разные сводки; держатель `work.sites.all` без назначения
  не получает; два прогона такта — одна сводка

## 2. PR 2: очередь приёмки и каталог пометок

- [ ] 2.1 Каталог пометок `Review/ReviewMarks.cs` с кодами `edited-after-submit`, `same-person`,
  `manager-edit`, `site-not-assigned`, `photos-pending`, `late-delivery` и источником каждого [WORK-28]
  [MOB-21] [WORK-34]. Проверка — юнит-тест: у каждого кода текст и источник
- [ ] 2.2 `GET /api/work/review/queue`: отбор по стройке, датам, сотруднику и коду; `marks` и `actions`
  у строки; `counts` по всем кодам по всему отбору [WORK-28] [WORK-39]. Проверка — `Work/ReviewQueueTests`:
  счётчик 70 при странице 50, руководитель без `actions`, монтажник 403, отчёт чужой стройки
  отсутствует
- [ ] 2.3 Граница выдачи `boundary` в ответе очереди (вид `work-07`) и пустая очередь с `reason`:
  `not-assigned`, `nothing-pending`, `filtered-out` [WORK-28] [WORK-46] [CORE-24.3]. Проверка — тест на
  каждую причину; «отчёты ваших строек (4)» не меняется от отбора по стройке, у руководителя — «все стройки»
- [ ] 2.4 Типы ответа и `WORK_MARK_CODES` в `src/client/src/shared/api/work.ts`; серверная сверка
  каталогов [WORK-28]. Проверка — `Work/ReviewMarkCatalogTests` падает на лишнем коде с любой стороны и
  на пустом списке клиента
- [ ] 2.5 Описать адреса очереди и приёмки в листинге REST API `docs/DEV_NOTES.md`. Проверка —
  маршруты в листинге совпадают с `Endpoints` модуля

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Work.Review|FullyQualifiedName~NotificationAudience|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ActivityLogInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
