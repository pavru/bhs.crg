# Tasks: сводка «сотрудник × стройка × день»

## 1. PR 1: адрес сводки

- [ ] 1.1 `Summary/SummaryStates.cs` и `WORK_SUMMARY_STATES` в `src/client/src/shared/api/work.ts`
  [WORK-29]. Проверка — `Work/SummaryStateCatalogTests` зелёный; лишний код на клиенте его роняет
- [ ] 1.2 `Summary/SummaryLayout.cs` — раскладка «сотрудник × стройка», сутки и «будущий день» по поясу
  стройки, «окно открыто» правилом `work-15` [WORK-29] [WORK-8] [WORK-54]. Проверка —
  `Work/SummaryLayoutTests`: «не поступал» ≠ «не назначен», Владивосток в 20:00 по Москве, две смены
  в сутках, `not-employed`, `zero-hours`
- [ ] 1.3 `Summary/SummaryEndpoints.cs` — `GET /api/work/summary`: период до 31 дня, отбор по стройке,
  чтение изолированным методом с параметром доступа, `boundary`, `boundaryOutcome`, `reason:
  not-assigned`, `contact: { state: 'not-tracked' }` [WORK-29] [WORK-39] [WORK-40] [CORE-24.3].
  Проверка — `Work/SummaryEndpointTests`: чужая стройка — 404, 32 дня — 400, «Руководитель» видит все
  стройки
- [ ] 1.4 Ворота: «любое из» `work.report.review`, `work.report.read.all`, отказ выключенного модуля
  [AUTH-8] [AUTH-9]. Проверка — `EndpointGateInventoryTests` зелёный; монтажник — 403; хост без
  `work` — 501
- [ ] 1.5 Запись `draft` без содержимого [WORK-54]. Проверка — тест сериализации: у `draft` нет полей
  часов и идентификатора отчёта
- [ ] 1.6 Замер ответа на посеве 200 строк × 31 день; число — в описании PR [WORK-29]. Проверка — ответ
  меньше 1 МБ или заведена постраничность по строкам

## 2. Макеты

- [ ] 2.1 Раунд Р5: три варианта сводки при 1280 px, 31 дне и двух сменах в клетке, вид каждого кода и
  легенда; выбор владельца, ссылку вписать в `design.md` [WORK-29]. Проверка — ссылка на выбранный
  вариант в `design.md`

## 3. PR 2: экран сводки

- [ ] 3.1 `src/client/src/shared/api/workSummary.ts` и `features/work/summary/SummaryPage.tsx`: отбор
  стройки и периода, граница выдачи, `reason: not-assigned` словами [WORK-29] [CORE-24.3]. Проверка —
  vitest на пустую сводку с причиной
- [ ] 3.2 Сетка на `DataGrid` (`pinned={2}`, дни из ответа) и `SummaryCell.tsx`: ☾ у ночной всегда,
  деление только при двух записях [WORK-29] [WORK-4]. Проверка — vitest: одна ночная — без деления
  с ☾; две смены — две записи
- [ ] 3.3 `SummaryLegend.tsx`; `report` открывает карточку `work-21`, `draft` — подсказка с моментом
  автоподачи [WORK-29] [WORK-54]. Проверка — vitest: щелчок по `draft` карточку не открывает
- [ ] 3.4 Пункт «Сводка» в `navConfig.ts` раздела `work` с правом «любое из» `work.report.review`,
  `work.report.read.all` [AUTH-16.1]. Проверка — `navAccess.test.ts`: «Руководитель» видит пункт,
  «Монтажник» — нет
- [ ] 3.5 Живой прогон `src/client/e2e/work-summary-smoke.mjs`: посев с «не поступал», «не назначен» и
  ночной сменой; вписать в `test:e2e:ci` и `e2e/README.md` [WORK-29]. Проверка — прогон зелёный на
  стенде
- [ ] 3.6 `docs/USER_GUIDE.md`: раздел «Сводка» — что значит каждый код, ☾, черновик без содержимого,
  «связь не отслеживается»; `npm run toc` в `docs/tools` [WORK-29] [WORK-54]. Проверка — проверка
  содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Summary|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста (`table` — 0)
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 Живой прогон под ролями модуля: `node e2e/work-summary-smoke.mjs` — «Менеджер проекта» видит
  свою стройку, «Руководитель» — все, «Монтажник» пункта не видит
