# Tasks: серии записей журнала действий

Два PR (design, «Migration Plan»). Сервер идёт первым и поведения экрана не меняет: ни один вид ещё не
объявляет свёртку. Функциональный PR 2 — метка `version:minor`. Change предшествует change по экрану
счёта и его автосохранению.

## 1. Объявление свёртки (PR 1)

- [x] 1.1 `ModuleActivityFold` и `ModuleFoldRule` в `BHS.CRG.Modules/Ports/IModuleActivityLog.cs`. Их
  принимает `ModuleActivityAction(Code, Title, ReadPermission?, Fold?)` — необязательный параметр, вызовы
  модулей не меняются. В ядре — `ActivityFold` и `FoldRule` в `Application/Activity`, а
  `ActivityAction(Code, Title, Fold?)` получает необязательное поле (D4) [CORE-28.3]. Проверка —
  `dotnet build BHS.CRG.slnx` без правок модулей, `ModuleBoundaryTests` зелёный.
- [x] 1.2 `ActivityActionCatalog` переводит `ModuleActivityFold` в `ActivityFold` и проверяет при старте
  (D4, проверки 1–3) [CORE-28.3]:
  - группа своего модуля;
  - одно право чтения на группу;
  - запретные виды ядра (роли, пользователи, права, состав модулей, закрытие периода).

  Проверка — тесты каталога: чужая группа, разные права в группе, свёртка у `core.modules.changed`.
  Каждый случай роняет сборку каталога с названной причиной, а годное объявление проходит.
- [x] 1.3 `ModuleActivityLogPort` передаёт в `IActivityLog` вид из каталога по коду, вместе с `Fold`
  (D4) [CORE-28.3]. Проверка — тест: модульный вид с объявленной свёрткой доходит до `RecordAsync` с
  `Fold`.

## 2. Сессия при записи (PR 1)

- [x] 2.1 `ActivityRecord`: поля `SessionId` и `SeriesHead`. Фабрика принимает их, сеттеров нет. Миграция
  ядра (D8) [CORE-28.2]:

  ```
  dotnet ef migrations add ActivitySeries --project src/server/BHS.CRG.Infrastructure \
    --startup-project src/server/BHS.CRG.Api --context AppDbContext
  ```

  Состав миграции: колонки `session_id` и `series_head`, заполнение старых строк (`session_id = id`,
  `series_head = true`), индексы `(target_id, occurred_at desc, id desc)`, `(session_id)` и частичный
  `(occurred_at desc, id desc) WHERE series_head`.

  Проверка — на пересозданной базе старт применяет миграцию. Тест миграции: прежняя запись —
  одиночная голова.
- [x] 2.2 `ActivitySession` — константы окна (10 минут, 60 минут) и правило «продолжает ли» (D2)
  [CORE-28.2]: тот же автор, не `null`, одна группа, пауза, интервал, дата по поясу компании.
  Проверка — модульные тесты правила на каждое условие: граница ровно в 10 и 60 минут; полночь по
  поясу, отличному от UTC.
- [x] 2.3 `ActivityLog.RecordAsync` (D3) [CORE-28.2]:
  - запись с целью — транзакция и `pg_advisory_xact_lock` по цели;
  - сворачиваемый вид с автором — чтение предыдущей записи цели и решение `ActivitySession`;
  - остальное — одиночная голова.

  Запрос замка живёт в `ActivityLog.cs`, а `ActivityLogInventoryTests` остаётся без новых строк.

  Проверка — интеграционные тесты (`IntegrationTestFixture`) по сценариям [CORE-28.2] «Сессия правки
  определяется при записи»: подряд, чередование группы, другой автор, вид вне группы, пауза, запись без
  автора и без цели. Тест параллели: две записи разных авторов одной цели одновременно — ни одной
  склейки через чужую. Время записи под замком замерено и названо в описании PR.

## 3. Чтение рядами и записи серии (PR 1)

- [x] 3.1 `IActivityLog.ReadAsync` и `CountAsync` идут по головам. Добор хвостов — одним запросом по
  `SessionId` и `Action`. Слияние `Before` и `After` по `FoldRule` (`Span`, `Union`) (D5) [CORE-28.2].
  `LastAsync` и `ExportAsync` по-прежнему сырые.

  Проверка — интеграционные тесты по сценариям «Журнал читается рядами серий»:
  - ряд ×9 с интервалом;
  - сменившаяся метка цели;
  - стабильность второй страницы, пока растёт серия на первой;
  - общее число по рядам;
  - отбор по действию;
  - старые записи одиночными.

  Сверка состава модулей (`ModuleCompositionJournal`) зелёная.
- [x] 3.2 `GET /api/activity` отдаёт `ActivityRowDto`: прежние поля, `count`, `lastAt`, `firstTarget`,
  `sessionId` (только если `count > 1`). `TargetId` не отдаётся (D5) [CORE-28.2]. Проверка —
  интеграционный тест: читатель с `core.audit.read` без `costs.invoice.read` не видит ни ряда, ни его
  записей, общее число их не учитывает. `CostsJournalMoneyTests` зелёный.
- [x] 3.3 `GET /api/activity/series/{sessionId}?action=` под `core.audit.read`, с той же видимостью.
  Пусто — 404, одинаковый для «нет» и «скрыта» (D6) [CORE-28.2]. Адрес только читает, поэтому If-Match
  не нужен — исключение названо в D6. Проверка:
  - интеграционные тесты сценариев «Записи ряда раскрываются»: раскрытие, скрытая сессия — 404, без
    `core.audit.read` — 403;
  - `EndpointGateInventoryTests` зелёный, новый адрес в описи ворот.

## 4. Копия (PR 1)

- [x] 4.1 `BackupActivityRecord`: `SessionId` и `SeriesHead`, оба необязательные. Импорт без них — как
  одиночная голова. Восстановление по-прежнему пропускает известные `Id` (D7) [CORE-28.2] [CORE-26].
  Проверка — `BackupManifestCoverageTests` зелёный. Тесты:
  - копия, снятая посреди серии, на чистом экземпляре даёт те же ряды;
  - на работающем — без дублей;
  - копия старого формата восстанавливается одиночными рядами без отказа.
- [x] 4.2 Документация: `docs/DEV_NOTES.md` — раздел «Серии журнала действий» (почему ключ при вставке,
  окно, замок, запретный перечень). `docs/ADMIN_GUIDE.md` — строка о свёрнутых рядах на экране журнала.
  Проверка — `npm run toc` в `docs/tools` не меняет содержание, или содержание перестроено.

## 5. Экран и объявления costs (PR 2)

- [x] 5.1 `shared/api`: тип `ActivityRow`, хук `useActivitySeries(sessionId, action)` (D9) [CORE-28.2].
  Проверка — `npx tsc -b`; vitest на разбор ответа: ряд с `count = 1` без `sessionId`.
- [x] 5.2 `ActivityPage.tsx` (D9) [CORE-28.2]:
  - ряд с `count > 1` — «×N · 14:02–14:19» и «была …»;
  - раскрытие — вложенные записи, компонентами `shared/ui` по `docs/UI.md`.

  Проверка — vitest экрана: ряд и раскрытие. `npm run ui:ratchet` не растёт. Снимок экрана — в описании
  PR.
- [x] 5.3 Объявления свёртки в `costs` [COST-6] [CORE-28.3]:
  - `InvoiceActions.Changed` — группа `costs.invoice.edit`, `Union` / `Union`;
  - `InvoiceActions.Lines` — та же группа, `Span` / `Span`;
  - `WaybillActions.Changed` и `WaybillActions.Lines` — группа `costs.waybill.edit`, `Span` / `Span`.

  Проверка — интеграционные тесты сценариев [COST-6] [CORE-28.3]:
  - сессия правки черновика даёт два ряда;
  - «Разобран» и самовозврат в черновик стоят отдельными рядами между рядами правки;
  - ряд «Счёт изменён» без значений полей;
  - «Всё верно» три раза — три ряда.
- [x] 5.4 Тест-сторож `ActivityFoldInventoryTests`: закрытый словарь «код вида → причина» по ядру и всем
  модулям. Новый сворачиваемый вид не проходит без строки (D4) [CORE-28.3]. Проверка — тест зелёный с
  четырьмя видами `costs`; временное объявление свёртки у `costs.invoice.parsed` его роняет и называет вид.

## 6. Верификация

- [x] 6.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter
  "FullyQualifiedName~Activity|FullyQualifiedName~Backup|FullyQualifiedName~CostsJournal|FullyQualifiedName~EndpointGateInventory"`
  — зелёный. Весь набор — `bash scripts/backend-tests.sh`.
- [x] 6.2 `cd src/client && npx tsc -b` и `npm test` — зелёные. `cd src/client && npm run ui:ratchet` не
  растёт.
- [x] 6.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` —
  зелёный. Ни один файл не перешагнул 500 строк кода. Похудевшие опущены в `src/file-size-baseline.json`.
- [ ] 6.4 Границы к прогону на стенде, на пересозданной базе:
  1. Один человек 25 минут правит черновик (шапка, строки, шапка…) с паузой 12 минут — две сессии по два
     ряда. Число правок совпадает с раскрытыми записями.
  2. Посреди серии А: Б жмёт «Всё верно», заканчивается распознавание, правка суммы роняет разобранный
     счёт в черновик. Каждое событие стоит отдельным рядом, склеек через него нет.
  3. Две вкладки и второй пользователь пишут один счёт. Склеек через чужую запись нет. Время записи
     названо.
  4. Читатель без права на счета не видит рядов, и число то же.
  5. Копия посреди серии восстановлена на чистый экземпляр — ряды те же.

  Итог — в описании PR 2.

## Workflow follow-up

- После слияния PR 2 — архив change (`/opsx:archive core-journal-series`). Если `core-activity-channel`
  ещё не реализован, его design дополняется условием «смена канала рвёт сессию» (D2).
- Change по экрану счёта (вариант Г) и его автосохранение начинаются после архива.
