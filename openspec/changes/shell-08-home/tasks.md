# Tasks: Главная «Продолжить работу»

## 0. До кода

- [ ] 0.1 Решения владельца и Архитектора на В-2 (объявление очереди) и В-3 (хранение «Недавних»)
  вписаны в `design.md` (В-1 решён 22.10.2026 — вариант Б, вписан в `plan-02-navigation` и
  `work-03-navigation`). Проверка — Open Questions содержит ответы, задачи ниже сверены с ними
- [ ] 0.2 Разбор с Архитектором (раздел рейла без подпанели в `shared/ui`) и Дизайнером (телефон, пустые
  состояния — их нет на артборде «B · Главная») — комментарием к #1436 (`docs/UI.md`, «Новый экран:
  порядок», шаг 1). Макет выбран решением эпика — новых вариантов не нужно. Проверка — ссылка на
  комментарий в `design.md`

## 1. PR 1: раздел и страница

- [ ] 1.1 `navConfig.ts`: `RailId` += `'home'`, первым, `unscoped`, признак `panel: false`; пункт `/` без
  права; `AppShell` не рисует подпанель, `BottomNav` ставит «Главную» первой [UI-33]. Проверка —
  `railSections.test.ts`, `phoneShell.test.tsx`
- [ ] 1.2 Индекс-маршрут `App.tsx` зовёт `entryPage` с Главной — пунктом ранга `HOME_ENTRY_RANK` (В-1 решён: вариант Б);
  живые прогоны, начинавшие с `/`, — на явный адрес [UI-33]. Проверка — vitest маршрута; `routing-smoke`
- [ ] 1.3 `features/home/HomePage.tsx`: заголовок через `PageHeader`, подзаголовок, блоки по порядку;
  пользователь без модулей видит «Недавние» и «Полезно знать» [UI-33]. Проверка — `HomePage.test.tsx`

## 2. PR 1: «Ждут вас» и очередь адресом

- [ ] 2.1 Экран счетов принимает `?queue=` (`parsing`, `unrecognized`, `lost`, `archived`), чип пишет его
  заменой записи истории, незнакомое значение — без отбора [COST-6.2]. Проверка — vitest страницы;
  обновление страницы сохраняет отбор
- [ ] 2.2 Реестр очередей по ответу В-2 (рекомендация — `shared/ui/homeQueues.ts` и
  `features/costs/homeQueues.ts`): числа — `useParsingCount`, `useInvoiceQueues`; видимость — тем же
  правилом, что чип на экране [UI-34]. Проверка — `homeQueues.test.ts`: у каждой очереди экран
  принимает `href`
- [ ] 2.3 Карточки: только непустые и открытые; «?» при `doubt`; отказ — «не посчитано»; все пусты —
  «Сейчас ничего не ждёт» [UI-34]. Проверка — `HomePage.test.tsx`; роль только с накладными карточек
  счетов не видит

## 3. PR 1: «Выполняется» и «Полезно знать»

- [ ] 3.1 «Выполняется» — `useActiveJobs()` и `JobRow`, блок только при задачах [UI-36]. Проверка —
  vitest: без задач блока нет
- [ ] 3.2 «Полезно знать» — три строки по правам; ключ `UserSettingKeys.HomeTips` (`home.tips`:
  `shown`/`hidden`), «Скрыть» через `useSyncedPreference`; вернуть — в профиле [UI-37] [CORE-25.3].
  Проверка — vitest: без `costs.invoice.edit` строки о сканах нет; серверный тест: незнакомое значение
  ключа — 400

## 4. PR 2: «Недавние»

- [ ] 4.1 По ответу В-3 (рекомендация — таблица ядра): `user_recent_records`, миграция
  `dotnet ef migrations add UserRecentRecords --project src/server/BHS.CRG.Infrastructure
  --startup-project src/server/BHS.CRG.Api --context AppDbContext` [CORE-25.3]. Проверка — на
  пересозданной базе старт применяет миграцию
- [ ] 4.2 `POST /api/account/recent` (204) и `GET /api/account/recent`: только свои, не больше 20,
  повтор поднимает; личные адреса в `EndpointGateInventoryTests`; без `If-Match` — названное исключение
  (`design.md`) [CORE-25.3] [AUTH-9]. Проверка — `RecentRecordsTests`: повтор, вытеснение, чужого не
  получить, без входа — 401
- [ ] 4.3 Разрешение при чтении через владельца типа (то же объявление, что поиск `shell-05`): удалённые,
  закрытые правами и записи выключенного модуля не отдаются [CORE-25.3]. Проверка — `RecentRecordsTests`;
  версия счёта после отметки не меняется
- [ ] 4.4 `useRecentTouch` на экранах из `RECORD_ROUTES` (дописать стройку, комплект, набор данных,
  шаблон); перепись: каждый такой экран отмечает открытие [UI-35]. Проверка — `recordRoutes.test.ts`
- [ ] 4.5 Блок «Недавние»: название, раздел и стройка, «когда»; пусто — словами [UI-35]. Проверка —
  `HomePage.test.tsx`; на другом устройстве (второй контекст браузера в живом прогоне) счёт первым

## 5. Документы

- [ ] 5.1 `docs/UI.md`, «Оболочка»: Главная — раздел без подпанели, очереди объявляет модуль, числа — те же
  запросы; `docs/USER_GUIDE.md` — Главная и что открывается после входа (оглавление — `cd docs/tools &&
  npm run toc`). Проверка — ревью PR; проверка «Frontend» (оглавление) зелёная

## 6. Верификация

- [ ] 6.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RecentRecords|FullyQualifiedName~UserSetting|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~InvoiceScanList"` — зелёный
- [ ] 6.2 `cd src/client && npx tsc -b` — без ошибок; `npm test` — зелёный
- [ ] 6.3 `cd src/client && npm run ui:ratchet` и `npm run lint:ratchet` — без роста
- [ ] 6.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 6.5 Весь список живых прогонов CI зелёный (зонтик, D6); среди них вход → Главная → «Разобрать»
  открывает очередь «Разобрать»
- [ ] 6.6 PR с меткой `version:minor`; последний PR — «Closes #1436»
