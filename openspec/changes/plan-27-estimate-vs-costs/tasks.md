# Tasks: «план против факта» материалов

## 1. Сервер

- [ ] 1.1 Разнесено и оплачено по номенклатуре стройки (раздела) путём `CostViews`; строки без
  номенклатуры — одной строкой [COST-21] [COST-20]. Проверка — `EstimateVsCostsTests`: суммы по
  номенклатуре равны итогу «Затрат по стройке» на том же посеве
- [ ] 1.2 `GET /api/costs/estimate-vs-costs` под `costs.report.read`: смета из `IPlannedWorks` (часть
  `cost`), объединение строк, расхождение по разнесённому [COST-21] [COST-26]. Проверка — тот же тест:
  +20 000; материал вне сметы; сметный без закупки; без права отчёта — 403; `EndpointGateInventoryTests`
  зелёный
- [ ] 1.3 Состояния: `hidden` без `plan.estimate.cost` (без итогов), `not-connected`, `not-in-estimate`
  [PLAN-11] [COST-21]. Проверка — тот же тест на три состояния
- [ ] 1.4 Выгрузка XLSX видимого, скрытое правами не выгружается [COST-22] [CORE-33]. Проверка — тест
  выгрузки под «Бухгалтером»: сметной колонки в файле нет

## 2. Экран (макеты П6)

- [ ] 2.1 Раунд П6 (общий с `plan-26`): три варианта вкладки «План против факта» в Claude Design, выбор
  владельца; ссылку вписать сюда [COST-21]. Проверка — ссылка на выбранный вариант в `design.md`
- [ ] 2.2 `shared/api/estimateVsCosts.ts` и вкладка `features/costs/EstimateVsCosts.tsx` в `SiteCostsPage.tsx` на `DataGrid`;
  состояния колонки словами [COST-21]. Проверка — vitest: `hidden` — подпись с правом; `not-in-estimate` —
  «нет в смете», а не 0
- [ ] 2.3 `docs/USER_GUIDE.md`: «план против факта», почему сравнение по разнесённому;
  `cd docs/tools && npm run toc` [COST-21]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~EstimateVsCosts|FullyQualifiedName~SiteCosts|FullyQualifiedName~EndpointGateInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
