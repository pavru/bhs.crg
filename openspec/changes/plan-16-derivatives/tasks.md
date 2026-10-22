# Tasks: производные редакции — «смета изменилась» и обновление КП

## 1. Признак расхождения

- [ ] 1.1 `Offers/OfferDrift.cs`: `drift` КП из сметы по исходной, новейшей и действующей редакциям; у КП по
  прайс-листу — нет [PLAN-13]. Проверка — тест: новая редакция → `newer-revision`, смена действующей →
  `current-changed`, без изменений → `none`
- [ ] 1.2 Коды `newer-revision`, `current-changed` вписаны в каталог пометок модуля (D17). Проверка — сторож
  каталога кодов зелёный

## 2. Предпросмотр и обновление КП

- [ ] 2.1 `GET /api/plan/offers/{id}/rebase-preview` под `plan.offer.edit`: `added`, `removed`, `changed`,
  `unmatched`, `fingerprint`; себестоимость — только с `plan.estimate.cost` [PLAN-13] [PLAN-11]. Проверка —
  тест: одна расценка в двух разделах сопоставляется по разделу и номеру; без `plan.estimate.cost` полей
  себестоимости нет
- [ ] 2.2 `POST /api/plan/offers/{id}/rebase` с `If-Match` и отпечатком; черновик — на месте, отправленное —
  новой версией [PLAN-13] [PLAN-12] [AUTH-8]. Проверка — тесты отказов: без заголовка 400, устаревшая версия
  409, чужой отпечаток 409 с новым предпросмотром, без права 403, модуль выключен — отказ;
  `RecordWriteGuardCoverageTests` зелёный
- [ ] 2.3 Наценка строки с парой сохраняется, снятые строки названы в ответе [PLAN-11]. Проверка — тест

## 3. Производные под сметой

- [ ] 3.1 `GET /api/plan/estimates/{id}/derivatives` под `plan.estimate.read`: блоки `offers` и `schedules`
  с `hidden` по правам, покрытие, `outdated`; цен продажи нет [PLAN-22] [PLAN-2]. Проверка — тест: «Сметчик»
  без `plan.offer.read` — блок КП `hidden` без чисел; покрытие 30 из 40 = 75 %
- [ ] 3.2 Блок графиков возвращает «графиков нет», пока не слит `plan-19`; после — работы графика по
  строкам редакции [PLAN-22]. Проверка — тест обновляется в `plan-19`

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~OfferDrift|FullyQualifiedName~OfferRebase|FullyQualifiedName~RevisionDerivatives|FullyQualifiedName~RecordWriteGuard"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
