# Design: контракт изоляции

Общая архитектура — `../work-00-epic/design.md` (D4 — параметр доступа, D12 — система не читает
опубликованное). Здесь только решения этого change.

## Context

Сверено по коду master `f8a8b222`; пересверено по `c1b92ca7` (13.11.2026: после `plan-22`, `work-10`,
`work-03`).

- `DataAccess` (`Application/DataSets/DataAccess.cs`) уже есть: кто, ключи, включённые модули,
  `OfSystem(reason)`. До поставщика набора он доходит (`ISystemDataProvider.ProvideAsync(..., access)`),
  до MCP — через `Api/Mcp/DataSnapshotTools.cs:23` (`DataAccessResolver`).
- `ModuleReader` (`BHS.CRG.Modules/Access/ModuleReader.cs`) и его построение из `DataAccess`
  (`Api/Modules/Ports/ModuleReadersPort.cs:25`, `From`) завёл `plan-22`; сверку имён с `DataAccess` уже
  держит `ModulePortMirrorTests` (без `EnabledModules`). Доккомментарий `ModuleReader` обещает, что
  `work-07` добавит изоляцию «НОВЫМИ полями», — решение ниже другое: охват в параметр не входит.
- Таблица модуля теряет его на последнем шаге: `ModuleTableQueries.Build(..., access.UserId!.Value)`
  (`Api/Modules/Tables/ModuleTableService.cs:74`, то же — `ModuleTableService.Shortcuts.cs:44`), служба
  строк видит `Guid UserId` (`BHS.CRG.Modules/Tables/ModuleTableQuery.cs:149`). Исключения на системе
  нет: ворота `OpenAsync` (`ModuleTableService.cs:176`) отказывают системе раньше, 409 с её причиной, на
  ЛЮБОЙ таблице — восклицательный знак держится только порядком проверок.
- Граница: `ModuleTable.Boundary` — одна строка (`ModuleTable.cs:157`), поставщик набора отдаёт её
  единственным исходом (`ModuleTableDataProvider.cs:37`). Выбор исхода уже есть у набора:
  `SystemDataSetDeclaration.BoundaryFor(chosen)` (`Application/DataSets/SystemDataSetDeclaration.cs:81`;
  `chosen` — ТЕКСТ исхода, не код) и поле `Boundary` результата разбора (`IDataSetParser.cs:37`).
- Экран показывает границу из ОПИСАНИЯ таблицы (`DescribeAsync`, `TableDeclarationDto.Boundary`;
  подзаголовок `features/tables/TableViewHeader.tsx:193`), то есть до чтения строк — исход там ещё не
  известен. Выгрузка XLSX границы не пишет вовсе (`ModuleTableExport.Preamble`): первая строка шапки —
  название таблицы.
- Строка по ключу вне отбора — не отказ: ответ 200 с пустыми строками, тот же, что на несуществующий
  ключ; панель строки говорит «под этим отбором такой строки нет» (`features/tables/RowPanel.tsx`).
- Таблиц с `PerUser` в поставке нет: `costs.invoices` — `None` (`Modules.Costs/Tables/InvoiceTable.cs:225`).
  Модуль `work` (`work-10`) таблиц ещё не объявляет — их заводит `work-20`.

## Goals / Non-Goals

**Goals:** параметр доступа доходит до службы строк на всех путях; граница выдачи — по исходу;
перепись, на которой `work-13` и `work-20` заводят изолированные записи и таблицы.

**Non-Goals:** чьи стройки у менеджера (`work-11`); таблицы `work` (`work-20`); проверка нарушением по
месту хранения (`work-25`); межмодульные контракты и сам `ModuleReader` — `plan-22-module-exchange`
(D18 зонтика): контракты `work-*` берут отсюда охват `ModuleReach`.

## Decisions

- **PR 1 — контракт.**
  - `BHS.CRG.Modules/Access/ModuleReader.cs` — готовый, из `plan-22` (D18 зонтика): `UserId?`, `Who`,
    `Keys`, `SystemReason`, `IsSystem`. Путь таблиц строит его тем же `ModuleReadersPort.From(DataAccess)`
    — второго построения нет. Полей не прибавляется: сверка имён в `ModulePortMirrorTests` не меняется,
    доккомментарий про «изоляцию новыми полями» переписывается под решение ниже.
  - `BHS.CRG.Modules/Access/ModuleReach.cs` — что вычисляет модуль: `Own` | `Sites(ids)` | `All` — общий
    язык охвата для таблиц, контрактов `work-*` и адресов модуля. Ядро охват не вычисляет: стройки
    менеджера — данные модуля (D5). Поэтому охват — не поле `ModuleReader`, а ответ модуля на вопрос
    «что этому читателю видно».
  - `ModuleTableQuery.UserId` → `Reader: ModuleReader`. `ModuleTablePage` получает
    `Outcome: ModuleTableOutcome(Code, Sites?)`.
  - `ModuleTable.Boundary: string` → `Boundaries: IReadOnlyList<ModuleTableBoundary(Code, Text)>`, текст
    с местом `{sites}`. `Problems()`: хотя бы одна граница, коды различны. Если граница одна, служба
    строк исход может не называть; если их несколько — обязана. Назвала исход — код обязан совпасть с
    объявленным и у одной границы: `ModuleTableBoundary.Only(text, code)` (по умолчанию `all`).
    Отказ на пути набора и MCP — тоже 409 с причиной (`ConflictException`), а не ошибка ядра; набор без
    подписи службы таблиц (выключенный модуль) не подписывается объявленной строкой с `{sites}`.
  - `ModuleTableService`: отказ системе остаётся в воротах `OpenAsync`, на любой таблице (D12: система
    опубликованного не читает), и называет таблицу и причину задания; служба строк системы не получает
    никогда. Ответ строк — `boundary` (подставленный текст) и `boundaryOutcome`; страница без исхода у
    таблицы с несколькими — отказ с названием таблицы (тот же приём, что `BoundaryFor`); исход, которого
    таблица не объявляла, — тоже. Описание (`DescribeAsync`) несёт границу, только если она одна: исход
    известен лишь по строкам, и угаданная подпись была бы хуже пустой. `ModuleTableExport.cs` — строка
    границы в шапке сразу за названием таблицы: название остаётся первой строкой файла.
    `ModuleTableDataProvider` — все тексты в `Declaration.Boundary`, применившийся — в `Boundary` результата.
  - Строка по ключу вне отбора по правам отвечает так же, как несуществующий ключ, — пустой выдачей
    (200, ни одной строки), а не 403 и не 404: так ядро отвечает на «такой строки нет» сегодня, и
    различить «чужая» и «нет такой» снаружи нечем.
  - `InvoiceTable.cs` — одна граница, поведение `costs.invoices` не меняется.
  - Клиент: `shared/api/tables.ts` — `boundaryOutcome?: string | null` у ответа строк, `boundary` описания
    — `string | null`; подзаголовок экрана (`TableViewHeader.tsx`) берёт границу из ответа строк, пока их
    нет — из описания.
- **PR 2 — перепись.**
  - `BHS.CRG.Modules/Data/IIsolatedRecord.cs` — пометка записи. `ModuleDbContext` не запрещает
    `Set<T>()`: запрет кодом обходится, перепись — нет.
  - `Tests/Configuration/IsolatedReadInventoryTests.cs` по образцу `PeriodClosureInventoryTests`: проекты
    через `SolutionModules.WithCore`; для каждой изолированной записи — нет `DbSet<T>` в контексте;
    `Set<T>()` и `FromSql*` / `SqlQuery*` / `ExecuteSql*` с её таблицей — только в файле из списка
    «файл чтения → причина»; модуль с `PerUser`-таблицей без изолированной записи — красный.
  - Самопроверка сторожа на образцах исходника — нарушение находится (`InventoryScopeTests` — что
    перепись видит модули).
- **Сторож.** PR 1 — `ModuleTableIsolationTests` на пробном модуле с `PerUser`-таблицей: четыре пути
  дают одни строки и одну границу; система — отказ; чужой ключ строки — тот же ответ, что на
  несуществующий; `ModuleTableCatalogTests` —
  объявление без границы и с повтором кода не стартует. PR 2 — `IsolatedReadInventoryTests`.
- **Чем ломается.** `HasQueryFilter` по текущему пользователю: фоновое задание читает ноль строк, а
  перепись обхода не видит (D4). Граница постоянной строкой: менеджер читает «все отчёты» там, где ему
  отдали четыре стройки.
- **Нового пишущего API нет** — If-Match и ворота модуля здесь не применимы; перепись ворот адресов не
  меняется.
- **Зависимости.** Нет входящих. Потребители: `work-11` (охват менеджера), `work-13` (пометка отчёта и
  строки), `work-20` (таблицы `PerUser`), `work-09` (тип параметра в контракте), `work-25`.

## Risks / Trade-offs

- [Перепись по тексту не видит имени таблицы, собранного склейкой] → то же ограничение, что у прочих
  переписей (`InventoryScopeTests`); сырой SQL по изолированной таблице допускается в одном файле.
- [До `work-13` у переписи нет живых подопечных] → самопроверка на образцах и правило «`PerUser` без
  изолированной записи — красный» делают её достижимой с первого объявления.
