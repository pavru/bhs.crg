# Tasks: подача отчёта, окно подачи и автоподача

## 1. Правило окна и файл примеров

- [ ] 1.1 `Submission/SubmissionWindow.cs`: `EndsAt`, `MomentOfManual`, `Suggested` — чистые функции
  [WORK-8] [WORK-10] [WORK-52] [MOB-35]. Проверка — модульные тесты: дневная смена — конец даты, ночная —
  конец следующих суток по поясу стройки; ручная подача после конца окна даёт конец окна
- [ ] 1.2 `Submission/SubmissionCases.json` и `SubmissionCasesTests`: каждый пример прогоняется; отдельный
  тест состава (ночная смена, пояс стройки ≠ пояс компании, «после полуночи в ночную смену», один пример
  `Europe/Berlin`) [MOB-18]. Проверка — тест зелёный; удаление ночных примеров роняет тест состава;
  изменение правила без файла роняет тест примеров

## 2. Хранение и подача

- [ ] 2.1 Колонки `window_ends_at`, `submitted_by`, `required_missing`, индекс `(status, window_ends_at)`,
  миграция `dotnet ef migrations add SubmissionWindow --project src/server/BHS.CRG.Modules.Work --context WorkDbContext`
  с расчётом окна у прежних отчётов [WORK-52]. Проверка — на пересозданной базе и на базе с черновиками
  миграция применяется, у прежних черновиков окно не пусто
- [ ] 2.2 Пересчёт окна в пути сохранения `work-13` при смене даты, смены или стройки; смена пояса стройки
  записанных окон не трогает [WORK-52] [WORK-8]. Проверка — тесты: переключение на ночь сдвигает окно на
  сутки; смена пояса стройки оставляет окна прежних черновиков, новый черновик — по новому поясу
- [ ] 2.3 Статусы `WORK-9` и запрет менять дату, смену и стройку вне `draft` и `returned` [WORK-9] [MOB-35].
  Проверка — тесты: подача принятого — 409; смена у поданного — 409
- [ ] 2.4 `POST /api/work/reports/{id}/submit` под `work.report.own` с `If-Match`, изоляция «свои»;
  обязательность места и материалов по признакам вида работы; `alreadySubmitted` у поданного [WORK-6]
  [WORK-10] [AUTH-8] [AUTH-9] [WORK-39]. Проверка — тесты: без `If-Match` — 400; устаревшая версия — 409;
  чужой отчёт — 404; `work.report.review` без `own` — 403; нет места у вида «требует места» — 422 со
  строкой и полем; уже поданный — 200 `alreadySubmitted: true`; `EndpointGateInventoryTests` зелёный
- [ ] 2.5 Правка строк поданного до приёмки разрешена автору и пишется журналом `work-14` [WORK-11].
  Проверка — тест: правка объёма после автоподачи даёт запись `afterSubmission: true`
- [ ] 2.6 Ответ чтения: `autoSubmitAt`, `submittedAt`, `submittedBy`, `requiredMissing` [WORK-10]
  [WORK-54]. Проверка — тест ответа ночного черновика и поданного автоподачей

## 3. Автоподача

- [ ] 3.1 `Submission/AutoSubmitJob.cs` (`IModuleJobHandler`) и объявление такта `work-08` с постоянной
  целью; момент — конец окна; возвращённые не трогаются; конфликт версии — пропуск до следующего такта
  [WORK-10] [WORK-41]. Проверка — тесты на подставном `TimeProvider`: простой три дня даёт моменты окон, а
  не время подъёма; два одновременных такта — одна подача и одна запись журнала на отчёт; возвращённый
  остаётся `returned`
- [ ] 3.2 От имени системы: запись журнала с автором «Система» и видом `auto-submitted`, одна запись ядра
  на прогон, параметр доступа «система с причиной» [WORK-41] [CORE-24.1] [CORE-28]. Проверка — тест
  журнала; `ActivityLogInventoryTests` зелёный
- [ ] 3.3 Автоподача при незаполненных обязательных полях: подаёт и пишет `requiredMissing` и запись
  журнала [WORK-6] [WORK-10]. Проверка — тест черновика без места к концу окна

## 4. Предлагаемая пара и документы

- [ ] 4.1 `suggested: { date, shift, label }` в `GET /api/work/sites/mine` по `SubmissionWindow.Suggested`,
  подпись — в файле примеров [MOB-35] [WORK-8]. Проверка — тесты: 02:00 при ночном черновике за вчера —
  «вчера · ночь», `label` «Ночная смена 16.11», `today` — 17.11; 08:00 без ночного — «сегодня · день»
- [ ] 4.2 `POST /api/work/entry/reports/{id}/submit` под `work.report.review`, параметр доступа «стройки»
  [WORK-31] [AUTH-8] [AUTH-9]. Проверка — тесты: менеджер подаёт введённый им отчёт; чужая стройка — 404;
  без `If-Match` — 400; устаревшая версия — 409; `work.report.own` без `review` — 403
- [ ] 4.3 `docs/USER_GUIDE.md`: окно подачи дневной и ночной смены, автоподача, что значит «подан
  автоматически: не заполнено»; `cd docs/tools && npm run toc`. Проверка — проверка «Frontend»
  (содержание) зелёная

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Submission|FullyQualifiedName~AutoSubmit|FullyQualifiedName~ReportJournal|FullyQualifiedName~ActivityLogInventory|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
