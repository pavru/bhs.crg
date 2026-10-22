# Tasks: коммерческое предложение из редакции

## 1. PR 1: модель и наценки

- [ ] 1.1 `Data/Offer.cs`, `Data/OfferLine.cs`, `OfferSectionMarkup`, миграция `Offers`
  (`--context PlanDbContext`); номер «КП-<год>-<NNN>»; заказчик — роль `role.organization` в `Needs`
  [PLAN-10] [PLAN-Q8]. Проверка — на пересозданной базе старт применяет миграцию; себестоимости в таблицах
  КП нет (тест по модели)
- [ ] 1.2 `Offers/OfferPricing.cs`: наценка строка → раздел → предложение, база по В-8 (ждёт решения В-8),
  округление, НДС «в том числе / сверху / без НДС» (ждёт решения В-9) [PLAN-11]. Проверка —
  `OfferPricingTests`: 20 % / 15 % / 10 % дают цену по самому частному уровню; итог = сумма строк
- [ ] 1.3 «Сделать КП из редакции» — одно действие, фиксирует редакцию (`plan-06`) [PLAN-10] [PLAN-5].
  Проверка — тест: после создания редакция зафиксирована этим КП; несопоставленные строки перенесены
- [ ] 1.4 Тип `КоммерческоеПредложение` (`PlanRecordTypes.cs`), тэг `offer.markup` в `PlanModule.Tags`,
  «Наценка» и «Итого» — `Locked` [TYPE-17] [CORE-20.2]. Проверка — `RecordWriteGuardCoverageTests` зелёный;
  запись итога в обход — поле не меняется; `PlanOnlyHostTests` стартует с тэгом
- [ ] 1.5 Деньги по правам: цены — `plan.offer.read`; себестоимость из сметы — только с
  `plan.estimate.cost`, иначе `hidden: ["cost"]` [PLAN-11] [PLAN-2]. Проверка — тест: без
  `plan.estimate.cost` в ответе нет ни одного поля себестоимости
- [ ] 1.6 Права `plan.offer.read`, `plan.offer.edit` с объяснениями и `ReadAllMark`; КП в
  `AccountingDocuments` [PLAN-23] [CORE-20.1]. Проверка — тест состава `/api/account/access` по ролям;
  смена валюты при существующем КП — 409 «коммерческих предложений: 1»
- [ ] 1.7 Адреса `GET/POST /api/plan/offers`, `GET/PUT/DELETE /api/plan/offers/{id}`, `PUT …/markups` с
  `If-Match` (кроме создания) [AUTH-8] [AUTH-9]. Проверка — тесты отказов: без `If-Match` — 400,
  устаревший — 409, «Сметчик» — 403, модуль выключен — 501; `EndpointGateInventoryTests` зелёный

## 2. PR 2: статусы и версии

- [ ] 2.1 Условие: `plan-12` слит (`ModuleRecordVersion`) [CORE-36]. Проверка — тип есть в master
- [ ] 2.2 `Data/OfferVersion.cs : ModuleRecordVersion`, миграция `OfferVersions`; разрешение в
  `RecordVersionInventoryTests` с причиной [CORE-36] [PLAN-12]. Проверка — перепись зелёная и видит
  наследника; `ExecuteUpdate` по таблице в пробе — красный
- [ ] 2.3 `Offers/OfferLifecycle.cs`: `send` (дата), `outcome` (принято / отклонено / без ответа), правка в
  `sent` — 409, `POST …/versions` — снимок в историю и черновик N+1 [PLAN-12]. Проверка —
  `OfferLifecycleTests`: правка отправленного — 409; новая версия — версия 1 в истории со статусом и датой
- [ ] 2.4 `GET …/versions` — номер, статус, дата отправки, итог по `plan.offer.read` [PLAN-12] [PLAN-22].
  Проверка — тест списка версий
- [ ] 2.5 Удаление — только черновик версии 1 без отправок, иначе 409 [PLAN-12]. Проверка — тест на обе ветки
- [ ] 2.6 Право `plan.offer.send` с объяснением; `send` и `outcome` под ним, с `If-Match` [PLAN-23] [AUTH-8]
  [AUTH-9]. Проверка — тест: `plan.offer.edit` без `plan.offer.send` — 403; тест состава доступа по ролям
- [ ] 2.7 `docs/ADMIN_GUIDE.md` (права `plan.offer.*`) и `docs/USER_GUIDE.md` (КП, наценки, версии);
  `npm run toc` в `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Offer|FullyQualifiedName~RecordVersionInventory|FullyQualifiedName~RecordWriteGuardCoverage|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~AccountAccess|FullyQualifiedName~PlanOnlyHost"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
