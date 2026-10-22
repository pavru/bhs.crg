# Tasks: бригада — участники, сверка и итог группы

## 1. PR 1: сервер

- [ ] 1.1 Колонка `crew_role` строки (`lead` | `member`), правило «участник назначен на стройку на дату»
  в сохранении строки, миграция схемы `work` [WORK-17] [WORK-Q7]. Проверка — тест: участник без
  назначения — 400 с ФИО; расходящиеся объёмы сохраняются как есть
- [ ] 1.2 `GET /api/work/constructions/{id}/crew-candidates?date=`: только идентификатор и ФИО, без
  `core.employees.read`; не назначен — пусто с `reason: not-assigned` [WORK-17]. Проверка —
  `Work/CrewCandidatesTests`: монтажник без `core.employees.read` получает ФИО, в чужой стройке — ни
  одного имени
- [ ] 1.3 Таблица `work.crew_groups` с уникальным ключом группы, группа заводится при подаче тем же
  `SaveChanges`; `GET /api/work/crew-groups` со `state`, `mismatch`, `actions`, числом неподанных
  [WORK-18] [WORK-54]. Проверка — тест: две смены — две группы, черновик участника в группу не входит
- [ ] 1.4 `POST /api/work/crew-groups/{id}/approve`: запись итога и журнал всех отчётов группы одним
  сохранением; `stale` после правки строки; разные единицы — 400 [WORK-18] [WORK-19] [WORK-51].
  Проверка — `Work/CrewTotalTests`: 120 + 120 + 100 → итог 120, у трёх отчётов запись журнала
- [ ] 1.5 `If-Match` и ворота утверждения: `perm:work.crew.approve`, стройка менеджера или
  `work.sites.all` [AUTH-8] [AUTH-9]. Проверка — `Work/CrewGateTests`: без заголовка 400, устаревшая
  версия 409, без права 403, чужая стройка 404, выключенный модуль 501; `EndpointGateInventoryTests`
  зелёный
- [ ] 1.6 Объявить `work.crew.approve` с объяснением и `ReadAllMark` [WORK-45]. Проверка — тест состава
  `/api/account/access` по системным ролям: «Менеджер проекта» получает право, остальные — без изменений
- [ ] 1.7 Код `crew-mismatch` в каталоге пометок и в `WORK_MARK_CODES`; пометка у отчётов группы с
  расхождением в `pending` и `stale` [WORK-18] [WORK-28]. Проверка — `ReviewMarkCatalogTests` зелёный;
  тест: после утверждения пометка снята
- [ ] 1.8 В группе нет часов [WORK-20]. Проверка — тест формы ответа: ни одного поля часов у группы и её
  строк

## 2. PR 2: панель «Сверка бригад» (макеты Р4)

- [ ] 2.1 Выбор владельца по макетам Р4 (или Р4б) вписан в `design.md`. Проверка — ссылка на выбранный
  макет в разделе «Состав PR»
- [ ] 2.2 `src/client/src/features/work/crew/CrewReconciliationPanel.tsx` на `DataGrid`: подсветка по
  `mismatch`, выбор итога из строк или ввод своего в `Modal`, кнопка только при `approve` в `actions`
  [WORK-18]. Проверка — vitest: без `approve` кнопки нет; `stale` показан как «требует решения»
- [ ] 2.3 `docs/USER_GUIDE.md` — глава «Сверка бригад»; содержание — `cd docs/tools && npm run toc`
  [WORK-18]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Work.Crew|FullyQualifiedName~ReviewMarkCatalog|FullyQualifiedName~AccountAccess|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
