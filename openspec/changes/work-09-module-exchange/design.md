# Design: межмодульный контракт и «Материалы на объекте»

Общая архитектура — `../work-00-epic/design.md` (D13 — контракт, D3 — право контракта, D4 — параметр
доступа). Здесь только решения этого change.

## Context

Сверено по коду master `311cfa6cd`.

- Механизм `plan-22-module-exchange` работает в `BHS.CRG.Modules/Exchange`; у `costs` уже
  зарегистрирован контракт «Фактические затраты».
- `Modules.Costs/Data/IssuedMaterials.cs` — перечень отпущенного по стройке с числом строк без
  номенклатуры; читает его только `GET /api/costs/materials` под `costs.waybill.read`
  (`Modules.Costs/Endpoints/WaybillEndpoints.cs`).
- `costs.materials.read` уже названо в `Api/Auth/SystemRoles.cs` у «Инженера ИД» и «Менеджера проекта».
  Право пропускается при наполнении ролей до объявления в `CostsModule`. У «Монтажника» уже есть
  `plan.estimate.hint`; для отпущенного нужен отдельный ключ `costs.materials.hint`.
- После `plan-21` `ModuleAccess.IsOpen` исключает права с признаком `Contract` из ворот модуля,
  по которым `/api/account/access` возвращает `available`.

## Goals / Non-Goals

**Goals:** поставщик контракта «Материалы на объекте» в двух видах ответа и права контракта для системных
ролей.

**Non-Goals:**
- подсказка в форме отчёта и ярусы «отпущено → смета → справочник» — `work-26`;
- набор данных «Материалы на объекте» для ИД (`COST-25`) — этап 6;
- «Плановые работы» и «Фактические работы» — этап 3 и `work-27`.

## Decisions

- **Основа из `plan-22-module-exchange`** (D18 зонтика). Снятый PR механизма покрыт действующей
  спецификацией `core`. Интерфейс называет поставщика в `[ModuleContract("costs", "Материалы на объекте")]`.
  `CostsModule.RegisterServices` регистрирует реализацию через `AddModuleContract`; каталог
  `Api/Modules/ModuleContractCatalog.cs` проверяет единственную регистрацию включённого поставщика.
  `IModuleContracts.Get<T>()` возвращает `Connected(Contract)` либо
  `NotConnected(Provider, ProviderTitle, ContractTitle)`, где `ProviderTitle` — название модуля.
  `ModuleContractRequestInventoryTests` следит за обращениями через порт.
- **Поставщик `costs`.**
  - `BHS.CRG.Modules/Exchange/MaterialsOnSite.cs` — `IMaterialsOnSite.ReadAsync(ModuleReader, Guid
    constructionId, ct)` возвращает `ContractAnswer<MaterialsOnSite>`. `Shown` содержит либо
    `Full(items: nomenclatureId, unit, quantity, first, last; unmatchedLines)`, либо
    `Hint(nomenclatureIds)` с уникальными идентификаторами. Названия потребитель читает портом каталога.
  - `Modules.Costs/Exchange/MaterialsOnSiteContract.cs` — над `IssuedMaterials.ReadAsync`; вид ответа —
    по ключам `ModuleReader`: `read` → полный, иначе `hint` → подсказка. До чтения общая проверка
    `ContractAnswer.Refusal` отвечает `Hidden(costs.materials.read)` либо `SystemRefused(reason)`.
  - `CostsModule.cs`: два права с видом «право контракта» (`plan-21`), объяснения прямо говорят, что
    модуль они не открывают; `ReadAllMark.Out` — «читать всё» сводит чтение модуля, а это выдача другому
    модулю.
  - `SystemRoles.cs` — «Монтажнику» `costs.materials.hint`.
  - `GET /api/costs/materials` остаётся под `costs.waybill.read`: экрану накладных право контракта не
    нужно.
- **If-Match и ворота.** Нового пишущего API нет. Ворота модуля — правом контракта не открываются (D3,
  сторож в тесте состава доступа).
- **Сторож.** `MaterialsOnSiteContractTests` (полный, подсказка, черновик не входит,
  «скрыто»), тест состава `/api/account/access` по ролям, живой хост `work` без `costs` получает «не
  подключено» (`WorkOnlyHostTests` из `work-10`), `CostsOnlyHostTests` зелёный.
- **Чем ломается.** Потребитель зовёт `GET /api/costs/materials` по HTTP: монтажник без
  `costs.waybill.read` получает 403, а выключенный `costs` — 501 вместо «не подключено». Право
  контракта объявлено обычным правом — у «Инженера ИД» появляется раздел «Счета и накладные».
- **Зависимости.** `plan-21` (вид «право контракта») и `plan-22` (механизм, `ModuleReader`).
  `work-10` (живой хост `work`) — для сторожа. Потребитель —
  `work-26`. Снимает вынос `STG-14` (`90-этапы.md:168`).

## Risks / Trade-offs

- [Подсказка выдаёт монтажнику, что отпущено на стройку] → без количеств, дат и денег (Р-3); ровно
  то, что ему и так предлагает поле.
