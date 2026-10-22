# Tasks: контракт «Фактические затраты»

## 1. Интерфейс и поставщик

- [ ] 1.1 `BHS.CRG.Modules/Exchange/ActualCosts.cs`: `[ModuleContract("costs", "Фактические затраты")]`,
  `IActualCosts.ReadAsync(ModuleReader, constructionId, sectionId?, ct)`, `Totals` с `linkedToWorks:
  not-tracked` [CORE-34] [PLAN-21.1]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 `Modules.Costs/Exchange/ActualCostsProvider.cs` тем же путём `CostViews`, что `SiteCosts`; без
  черновиков и отклонённых [COST-21] [COST-20]. Проверка — `ActualCostsContractTests`: два счёта, один
  оплачен — `allocated` и `paid`; `paid` равен «Затратам» отчёта на том же посеве; черновик не входит;
  отбор по разделу
- [ ] 1.3 Регистрация в `CostsModule.RegisterServices` [CORE-34]. Проверка — хост `plan` без `costs`
  получает `NotConnected`; `CostsOnlyHostTests` зелёный

## 2. Право контракта

- [ ] 2.1 `CostsModule.Permissions`: `costs.totals.read` с признаком контракта и `ReadAllMark.Out`;
  ответ по `costs.totals.read` или `costs.report.read`, иначе `Hidden` (ждёт решения В-3) [COST-28]
  [AUTH-8.1]. Проверка — тот же тест: только `costs.totals.read` — итоги; без прав — `Hidden`; система —
  отказ
- [ ] 2.2 `SystemRoles.cs`: «Менеджеру проекта» — `costs.totals.read` (ждёт решения В-3) [PLAN-21.1].
  Проверка — тест состава `/api/account/access` по системным ролям; `CostsOnlyHostTests` — число прав
  `costs.` и объяснение обновлены
- [ ] 2.3 `docs/ADMIN_GUIDE.md`: право `costs.totals.read`; `cd docs/tools && npm run toc` [COST-28].
  Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~ActualCosts|FullyQualifiedName~SiteCosts|FullyQualifiedName~CostsOnlyHost|FullyQualifiedName~AccountAccess|FullyQualifiedName~ModuleContract"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
