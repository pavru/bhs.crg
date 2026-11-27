# Tasks: заявки на закупку из материалов сметы

## 1. PR 1: заявка

- [x] 1.1 `Modules.Costs/Data/PurchaseRequest.cs`, `PurchaseRequestLine.cs`, конфигурация в
  `CostsDbContext`, миграция `PurchaseRequests`
  (`dotnet ef migrations add PurchaseRequests --project src/server/BHS.CRG.Modules.Costs --context CostsDbContext`)
  [COST-5.1]. Проверка — старт на чистой базе, обновление с контрольной накладной и восстановление
  заявки со строками и продолжением нумерации
- [x] 1.2 Права `costs.request.read` (`ReadAllMark.In`), `costs.request.edit` (`Out`) в `CostsModule`.
  Проверка [COST-28] — тест состава `/api/account/access` по
  ролям; `CostsOnlyHostTests` — число прав и объяснение. Менеджер проекта получает доступ к модулю
  с правами заявок. Реестр оставляет доступный отбор, причины скрытых сумм и ввод значения вручную.
  Справочник строек доступен читателям счетов и накладных, справочник статей — читателям счетов.
  Чип избранного без доступа к статьям сообщает, что тёзки среди статей не проверены.
- [x] 1.3 `POST /api/costs/requests/draft-from-estimate` через `IPlannedWorks` (часть `materials`) и
  `IssuedMaterials`: «по смете − отпущено», `issuedExceeds`, `unitMismatch`, «скрыто правами», `plan`
  выключен — 409 [COST-5.1]. Проверка — `PurchaseRequestTests`: 100 − 30 = 70; 120 отпущено — 0 и
  пометка; без права — `hidden`; без `plan` — 409. По решению от 27.11.2026 также проверить долю
  раздела стройки 40 из 100, совместный и раздельный выбор позиций, повторный расчёт, неизвестные
  и разные единицы, включая две строки без единицы в одной позиции перечня, и все предупреждения
  об охвате и отсутствии резервирования
- [x] 1.4 Чтение, сохранение, правка строк, переходы, удаление черновика под `costs.request.*` с
  `If-Match` [COST-5.1]. Проверка — тот же тест: без `If-Match` — 400, устаревшая — 409, правка
  поданной — 409, только чтение — 403, без модуля — 501. Параллельная замена строк и запись причины
  возврата в журнал с повтором после сбоя доставки. `RecordWriteGuardCoverageTests`,
  `EndpointGateInventoryTests` зелёные
- [x] 1.5 Объявления ссылок `CORE-34.1` (номенклатура, позиция перечня, стройка) в `CostsReferences.cs`
  [CORE-34.1]. Проверка — `ModuleReferenceInventoryTests` зелёный; удаление номенклатуры строки заявки —
  отказ с числом

## 2. PR 2: связь со счётом

- [x] 2.1 `Invoice.PurchaseRequestId`, миграция `InvoiceRequestLink`; правка шапки счёта ставит и снимает
  ссылку под `costs.invoice.edit` и `If-Match` счёта [COST-5.1]. Связь требует поданной заявки и полной
  разноски на её стройку. Проверки `InvoiceRequestLink_*`: отказ при неполной или смешанной разноске,
  защита при дальнейших правках и исправлении периода, включая снижение денежного допуска; удаление связанного черновика и восстановление
  связи из резервной копии — `PurchaseRequestTests`
- [x] 2.2 «Заказано / выставлено / отпущено по всей стройке» по номенклатуре и единице в чтении заявки
  [COST-5.1]. Регрессии `InvoiceRequestLink_*`: сумма двух связанных счетов без несвязанного, несовместимые
  и неизвестные единицы, неизвестный итог при связанном счёте без строк, менеджеру виден отпуск,
  право чтения счетов проверяется отдельно

## 3. PR 3: экраны (макеты П6)

- [ ] 3.1 Раунд П6: три варианта списка и карточки заявки, мастера «из сметы» и выбора заявки в счёте в
  Claude (артефакты типа «Design»), выбор владельца; ссылку вписать сюда [COST-5.1]. Проверка — ссылка на выбранный вариант
  в `design.md`
- [ ] 3.2 `shared/api/purchaseRequests.ts`: чтение, предложение, правка с `If-Match`, разбор
  400/403/409/422 [COST-5.1]. Проверка — vitest на разбор отказов
- [ ] 3.3 `features/costs/requests/*`: список на `DataGrid`, карточка, мастер (`Modal wide`), пометки
  `issuedExceeds`, `unitMismatch`, состояние «скрыто правами», выбор раздела стройки и предупреждения
  об общем отпуске и отсутствии резервирования; пункт «Заявки» в разделе затрат [COST-5.1].
  Проверка — vitest: `hidden` — подпись с правом, а не пустой список
- [ ] 3.4 Выбор заявки в шапке счёта [COST-5.1]. Проверка — vitest: заявки только своей стройки
- [ ] 3.5 Живой прогон `src/client/e2e/purchase-requests-smoke.mjs` под «Снабженцем»; вписать в
  `test:e2e:ci` и `e2e/README.md`. Проверка — прогон зелёный на стенде
- [ ] 3.6 `docs/USER_GUIDE.md`: заявки на закупку; `cd docs/tools && npm run toc` [COST-5.1]. Проверка —
  проверка содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PurchaseRequest|FullyQualifiedName~InvoiceRequestLink|FullyQualifiedName~RecordWriteGuard|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~CostsOnlyHost"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
