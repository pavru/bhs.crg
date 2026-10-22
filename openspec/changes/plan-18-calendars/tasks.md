# Tasks: календари компании и стройки

## 0. Условие

- [ ] 0.1 Ответ владельца на В-4 вписан в `docs/tz/50-планирование.md`; при «в ядре» таблицы и служба
  переносятся в ядро до кода, `design.md` правится. Проверка — в `design.md` нет «ждёт решения В-4»
- [ ] 0.2 Образец файла производственного календаря на год лежит в тестовых данных. Проверка — файл в
  `src/server/BHS.CRG.Tests/TestData/Calendars/`

## 1. PR 1: сервер

- [ ] 1.1 Таблицы `calendar_years`, `calendar_days`, `construction_calendars`, `construction_calendar_days`,
  миграция `Calendars` (`--context PlanDbContext`); ссылка `CORE-34.1` на стройку [PLAN-19.1]. Проверка —
  `ModuleReferenceInventoryTests` зелёный
- [ ] 1.2 `Calendars/WorkingDays.cs`: рабочий день, «плюс N рабочих дней» (в том числе отрицательное),
  «рабочих дней между», пометка `calendar-missing` [PLAN-19.1] [PLAN-16.1]. Проверка — `WorkingDaysTests`:
  майские праздники, перенос субботы, пятница перед праздниками, суббота стройки, незаполненный год
- [ ] 1.3 Разбор файла производственного календаря и предпросмотр (`WritesNothing`) [PLAN-19.1]. Проверка —
  тест на образце 0.2; испорченная строка — отказ с номером строки
- [ ] 1.4 Адреса `/api/plan/calendars`: чтение («любое из» `plan.schedule.read`, `plan.calendar.edit`),
  правка года и календаря стройки с `If-Match` [PLAN-19.1] [AUTH-8]. Проверка — тесты отказов: без
  заголовка 400, устаревшая 409, менеджер на календарь компании — 403, модуль выключен — отказ;
  `RecordWriteGuardCoverageTests` и `EndpointGateInventoryTests` зелёные
- [ ] 1.5 Объявить `plan.calendar.edit`, `plan.schedule.read`, `plan.schedule.edit` с `ReadAllMark`;
  «Менеджеру проекта» — `plan.schedule.*` по `PLAN-23` (ждёт решения В-4) [PLAN-23]. Проверка — тест состава
  `/api/account/access` по системным ролям; `PlanOnlyHostTests` — число прав обновлено
- [ ] 1.6 Зонтик `plan-00-epic/design.md`, D12: права графиков объявляет `plan-18`, а не `plan-19`.
  Проверка — `openspec validate plan-00-epic --strict` зелёный

## 2. PR 2: экраны (макеты П4)

- [ ] 2.1 Раунд П4 (часть «календари»): три варианта календаря компании и календаря стройки в Claude Design,
  выбор владельца; ссылку вписать в `design.md` [PLAN-19.1]. Проверка — ссылка в `design.md`
- [ ] 2.2 `shared/api/planningCalendars.ts`; `features/planning/calendars/CompanyCalendarPage.tsx` — год по
  месяцам, меню вида дня, загрузка файла с предпросмотром (`Modal wide`) [PLAN-19.1]. Проверка — vitest:
  без `plan.calendar.edit` действий нет; 409 — «год тем временем изменили — перечитайте»
- [ ] 2.3 `ConstructionCalendarPanel.tsx` в разделе «График» стройки: дни недели, сменность, исключения
  [PLAN-19.1]. Проверка — vitest на правку с версией
- [ ] 2.4 `docs/USER_GUIDE.md` (календарь стройки), `docs/ADMIN_GUIDE.md` (производственный календарь на
  год); `cd docs/tools && npm run toc`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkingDays|FullyQualifiedName~PlanCalendar|FullyQualifiedName~AccountAccess|FullyQualifiedName~RecordWriteGuard|FullyQualifiedName~PlanOnlyHost"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
