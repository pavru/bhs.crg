# Tasks: сопоставление видов работ и материалов

## 1. PR 1: виды работ

- [x] 1.1 `Data/RateMatch.cs` (ключ — отпечаток приведённого шифра + база, значение — вид работы, единица
  классификатора, коэффициент, единица расценки, `xmin`) и миграция `RateMatches`
  (`--context PlanDbContext`) [PLAN-6.1]. Проверка — тест: второй ключ с тем же отпечатком отвергнут
  единственным индексом
- [x] 1.2 Сопоставление строки: вид работы, коэффициент, позиция перечня портом `plan-04`, объём позиции =
  объём × коэффициент; несопоставленная — `unmatched` [PLAN-6.1] [PLAN-4] [CORE-13]. Проверка —
  `RateMatchingTests`: «100 м → м» с коэффициентом 100 даёт позицию в «м» и объём 1200 из 12
- [x] 1.3 Массовое «сопоставить раздел ЛСР» с ответом «сопоставлено / пропущено с причиной» [PLAN-6.1].
  Проверка — тест: строка другой единицы пропущена с `differentUnit`
- [x] 1.4 Запоминание после записи (`RememberSafelyAsync`, повтор при гонке), подстановка «запомнено»,
  `unit-changed` при расхождении единицы расценки [PLAN-6.1]. Проверка — тест: повторный импорт
  подставляет; изменённая единица — не подставляет
- [x] 1.5 Зафиксированная редакция: сопоставить несопоставленную — можно, пересопоставить — 409 (решение
      В-6) [PLAN-5]. Проверка — тест на обе ветки
- [x] 1.6 Адреса `PUT …/lines/{lineId}/match`, `POST …/sections/match`, список `rate-matches` с «забыть» и
  «перенацелить» под `plan.estimate.edit` с `If-Match` [AUTH-8] [AUTH-9]. Проверка — тесты отказов:
  без `If-Match` — 400, устаревший — 409, только `plan.estimate.read` — 403, модуль выключен — 501;
  `EndpointGateInventoryTests` зелёный

## 2. PR 2: материалы

- [x] 2.1 `Data/ResourceMatch.cs` (код ресурса старше наименования, значение — номенклатура) и миграция
  `ResourceMatches` [PLAN-9.1] [CORE-9]. Проверка — тест единственности ключа
- [x] 2.2 Сопоставление ресурса: похожие через `IModuleCatalog.SimilarAsync` (роль `role.nomenclature` в
  `Needs`), новая позиция — явным действием, «запомнено» с отменой [PLAN-9.1] [COST-7.1]. Проверка —
  `ResourceMatchingTests`: незнакомый ресурс несёт похожие, знакомый подставлен с пометкой
- [x] 2.3 Адрес `PUT …/resources/{resourceId}/match` и список `resource-matches` с `If-Match` [AUTH-8]
  [AUTH-9]. Проверка — тесты отказов 400/409/403
- [x] 2.4 `docs/USER_GUIDE.md`: сопоставление, «запомнено», коэффициент пересчёта; `npm run toc` в
  `docs/tools`. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [x] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RateMatch|FullyQualifiedName~ResourceMatch|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleReferenceInventory"` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок (клиент не менялся, проверка на всякий случай)
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
