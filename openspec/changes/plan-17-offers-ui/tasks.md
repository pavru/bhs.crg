# Tasks: экраны коммерческих предложений и прайс-листа

## 1. Макеты

- [ ] 1.1 Раунд П3: три варианта КП (список, карточка, наценки трёх уровней, статусы и версии, полоса
  «смета изменилась»), прайс-листа и блока производных в Claude Design, выбор владельца; ссылку вписать в
  `design.md` [PLAN-10] [PLAN-10.1] [PLAN-12] [PLAN-13] [PLAN-22]. Проверка — ссылка на выбранный вариант в
  `design.md`
- [ ] 1.2 Сверить с ответами `plan-13`, `plan-14`, `plan-16`, что каждый носитель экрана — поле сервера
  (`hidden`, `markupSource`, `drift`, `no-price`, `outdated`, `coverage`); недостающее дописать в
  серверный change. Проверка — перечень носителей в `design.md`

## 2. PR 1: КП

- [ ] 2.1 `shared/api/planningOffers.ts`: список, карточка, правка с `If-Match`, статусы, версии,
  предпросмотр и обновление, печать; разбор 400/403/409 [PLAN-10] [PLAN-12]. Проверка — vitest на разбор отказов
- [ ] 2.2 `features/planning/offers/OffersPage.tsx` и пункт рейла (механизм `plan-02`) [PLAN-20] [PLAN-10.1].
  Проверка — vitest: колонка цены есть только в ответе с ценами
- [ ] 2.3 `OfferCard.tsx`: строки, наценки трёх уровней, итог и НДС сервера (ждёт решения В-9), колонка
  себестоимости только при её наличии в ответе [PLAN-11]. Проверка — vitest: без `cost` колонки нет в разметке
- [ ] 2.4 `OfferStatusActions.tsx`, `OfferVersions.tsx`: отметка отправки с датой, итог, «Новая версия»;
  отправленное — только чтение [PLAN-12]. Проверка — vitest: без `plan.offer.send` действия нет; после
  отправки ячеек правки нет
- [ ] 2.5 `OfferDriftBanner.tsx`, `OfferRebaseDialog.tsx`: разница из предпросмотра, 409 по отпечатку —
  перечитать [PLAN-13]. Проверка — vitest на 409
- [ ] 2.6 «Сделать КП» в карточке сметы (`CreateOfferDialog.tsx`) и «Печать» (`plan-15`) [PLAN-10]
  [PLAN-14]. Проверка — vitest на заведение из редакции
- [ ] 2.7 Живой прогон `src/client/e2e/plan-offers-smoke.mjs`, посев КП в `e2e/seed.mjs`; вписать в
  `test:e2e:ci` и `e2e/README.md` [PLAN-12]. Проверка — прогон зелёный на стенде

## 3. PR 2: прайс-лист, КП без сметы, производные

- [ ] 3.1 `shared/api/planningPriceList.ts`, `features/planning/pricelist/PriceListPage.tsx`: действующая
  цена, история, правка с `If-Match` [PLAN-10.1]. Проверка — vitest: без `plan.pricelist.edit` действий нет
- [ ] 3.2 `PriceListOfferDialog.tsx`: пометка `no-price`, итог «неполный» [PLAN-10.1]. Проверка — vitest
- [ ] 3.3 `features/planning/estimates/EstimateDerivatives.tsx`: КП и графики по редакциям, покрытие,
  «устарело», `hidden` с названием права [PLAN-22]. Проверка — vitest: `hidden` без чисел
- [ ] 3.4 `docs/USER_GUIDE.md`: КП из сметы и по прайс-листу, наценки, статусы и версии, «смета
  изменилась», прайс-лист; `cd docs/tools && npm run toc` [PLAN-10]–[PLAN-13]. Проверка — проверка
  содержания в «Frontend» зелёная

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PlanOffer|FullyQualifiedName~PriceList|FullyQualifiedName~OfferDrift"` — зелёный (сервер не сломан)
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm test` и `npm run ui:ratchet` — зелёные, без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 Живой прогон под ролями: `node e2e/plan-offers-smoke.mjs` — «Менеджер проекта» делает и отправляет
  КП; «Сметчик» не видит раздела КП и цен продажи
