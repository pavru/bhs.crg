# Tasks: экраны графика

## 1. Макеты

- [ ] 1.1 Раунд П4 (часть «график»): три варианта списка работ, предшественников, предупреждений и
  диаграммы в Claude Design, выбор владельца; ссылку вписать в `design.md` [PLAN-17] [PLAN-16.1]
  [PLAN-20]. Проверка — ссылка на выбранный вариант в `design.md`
- [ ] 1.2 Сверить с ответами `plan-19` и `plan-18`, что длительность, предупреждения, `skipped.unmapped`,
  `distribution-changed` и нерабочие дни — поля сервера; недостающее дописать в серверный change.
  Проверка — перечень носителей в `design.md`

## 2. PR 1: список

- [ ] 2.1 `shared/api/planningSchedules.ts`: графики, работы, построение, перестроение, правка с `If-Match`;
  разбор 400/403/409/422 [PLAN-15] [PLAN-16]. Проверка — vitest на разбор отказов
- [ ] 2.2 `SchedulesPanel.tsx` — пункт «График» стройки (механизм `plan-02`), «Построить из сметы» с
  числом не вошедших строк [PLAN-20] [PLAN-15]. Проверка — vitest: без `plan.schedule.edit` действий нет
- [ ] 2.3 `ScheduleWorksGrid.tsx` на `EditGrid`: сроки, объём, исполнители, длительность и предупреждения
  из ответа, «по стройке в целом»; сводка предупреждений с отбором [PLAN-17] [PLAN-19]. Проверка — vitest:
  подменённый ответ с нарушением даёт знак у обеих строк; 409 сохраняет набранное
- [ ] 2.4 `shared/ui/EmployeePicker.tsx` (поиск портом `plan-03`, пометка архивных) [PLAN-16]. Проверка —
  vitest: архивный сотрудник не выбирается для новой работы
- [ ] 2.5 `RebuildByDistributionDialog.tsx`: предпросмотр, 409 по отпечатку — перечитать [PLAN-15].
  Проверка — vitest на 409

## 3. PR 2: зависимости и диаграмма

- [ ] 3.1 `ScheduleLinksCell.tsx`: работы своего и других графиков стройки, задержка, отказ цикла перечнем
  [PLAN-16.1]. Проверка — vitest: отказ с тремя работами называет все три
- [ ] 3.2 `ScheduleGantt.tsx`: SVG по той же модели, нерабочие дни, стрелки связей, предупреждения, переход
  к строке; цвета токенами [PLAN-17] [PLAN-19.1]. Проверка — vitest: щелчок по полосе выделяет строку;
  `npm run ui:ratchet` без роста

## 4. PR 3: печать списка и проверка

- [ ] 4.1 `Printing/ScheduleWorksPrintContext.cs`, объявление формы в `PlanModule.PrintForms` (право
  `plan.schedule.read`), шаблон по умолчанию, кнопка «Печать» (решение В-10) [PLAN-17] [CORE-21].
  Проверка — серверный тест контекста печати; `PlanOnlyHostTests` печатает график
- [ ] 4.2 Живой прогон `src/client/e2e/plan-schedule-smoke.mjs`, посев графика в `e2e/seed.mjs`; вписать
  в `test:e2e:ci` и `e2e/README.md` [PLAN-17]. Проверка — прогон зелёный на стенде
- [ ] 4.3 `docs/USER_GUIDE.md`: график, построение из сметы, зависимости, предупреждения, диаграмма,
  печать; `cd docs/tools && npm run toc` [PLAN-15]–[PLAN-17]. Проверка — проверка содержания в «Frontend» зелёная

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Schedule|FullyQualifiedName~RecordPrint|FullyQualifiedName~PlanOnlyHost"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm test` и `npm run ui:ratchet` — зелёные, без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 5.5 Живой прогон под ролями: `node e2e/plan-schedule-smoke.mjs` — «Менеджер проекта» строит и правит
  график; «Сметчик» без `plan.schedule.read` пункта «График» не видит
