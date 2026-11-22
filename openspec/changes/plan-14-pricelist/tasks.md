# Tasks: прайс-лист и КП без сметы

## 1. Прайс-лист

- [x] 1.1 `Data/PriceListEntry.cs` и миграция `PriceList` (`dotnet ef migrations add PriceList --project
  src/server/BHS.CRG.Modules.Plan --context PlanDbContext`): вид работы, цена, «действует с», уникальность
  пары; объявления ссылок `CORE-34.1` на `ВидРаботы` и сохранённую единицу [PLAN-10.1]. Проверка —
  `ModuleReferenceInventoryTests` зелёный; удаление вида работы с ценой — отказ с числом держателей
- [x] 1.2 Действующая цена на дату — одна функция модуля [PLAN-10.1]. Проверка — тесты на границы дат
  и смену единицы классификатора с сохранением единицы в истории
- [x] 1.3 Объявить `plan.pricelist.edit` (`ReadAllMark.Out`), «Менеджеру проекта» — по `PLAN-23` [PLAN-23].
  Проверка — тест состава `/api/account/access` по системным ролям; `PlanOnlyHostTests` — число прав обновлено

## 2. Адреса

- [x] 2.1 `GET /api/plan/pricelist` под `plan.offer.read` [PLAN-10.1] [PLAN-2]. Проверка — тест: «Сметчик» — 403
- [x] 2.2 `PUT /api/plan/pricelist/{workTypeId}`, `DELETE …/entries/{id}` под `plan.pricelist.edit` с
  `If-Match` [PLAN-10.1] [AUTH-8]. Проверка — тесты отказов: без заголовка 400, устаревший 409, без права
  403, модуль выключен — отказ; `RecordWriteGuardCoverageTests` зелёный

## 3. КП без сметы

- [x] 3.1 `POST /api/plan/offers/from-pricelist` (группа КП `plan-13`, `plan.offer.edit`): строки по видам
  работ, цена на дату, пометка `no-price`, итог «неполный» [PLAN-10.1]. Проверка — тест: вид работы без
  цены даёт строку без цены, а не 0; `null` в массиве строк даёт 400
- [x] 3.2 Носители `drift: null`, `canBuildSchedule: false` с причиной; действие «пересчитать по
  прайс-листу» в черновике с показом изменений [PLAN-10.1] [PLAN-13]. Проверка — тест: смена цены после
  заведения КП строк не меняет, пересчёт использует сохранённую единицу строки, изменение выбранной
  цены после предпросмотра даёт 409
- [x] 3.3 `docs/DEV_NOTES.md`: адреса прайс-листа и правило действующей цены (раздел `USER_GUIDE.md`
  пишет `plan-17` вместе с экраном). Проверка — ревью PR

## 4. Верификация

- [x] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~PriceList|FullyQualifiedName~.Plan.Offer|FullyQualifiedName~AccountAccess|FullyQualifiedName~RecordWriteGuard|FullyQualifiedName~ModuleReferenceInventory|FullyQualifiedName~PlanOnlyHost"` — зелёный
- [x] 4.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
