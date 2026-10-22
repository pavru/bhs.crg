# Design: журнал отчёта только на дописывание

Общая архитектура — `../work-00-epic/design.md` (D10, D4). Здесь только решения этого change.

## Context

- Отчёт, строки и протокол изменений `report_changes` — `work-13`. Протокол отвечает на вопросы «применён ли
  этот `changeId`» и «что изменилось после номера N». Содержимого правки в нём нет.
- Образец — книга счёта `costs`:
  - единственное место записи — `src/server/BHS.CRG.Modules.Costs/Data/InvoiceBook.cs`;
  - сущность — `Data/InvoiceBookEntry.cs` (автор идентификатором и именем снимком, `Seq`, `At`);
  - перепись второго места записи — `src/server/BHS.CRG.Tests/Configuration/InvoiceBookInventoryTests.cs`;
  - перепись `ExecuteUpdate` и сырого SQL — отдельный сторож
    `src/server/BHS.CRG.Tests/Configuration/InvoiceWritePathTests.cs` (`InvoiceBookInventoryTests.cs:12-14`).
- Запрет правки и удаления строк набора даёт `IAppendOnly` (`src/server/BHS.CRG.Modules/Data/IAppendOnly.cs`)
  в `ModuleDbContext`. Мимо отслеживания пометка не защищает (`IAppendOnly.cs:18-20`).
- Схема модуля целиком уходит в копию через `ModuleSchemaBackup` (`src/server/BHS.CRG.Api/Modules/ModuleSchemaBackup.cs`).

## Goals / Non-Goals

**Goals:**
- Журнал одной транзакцией с отчётом.
- «Правка после подачи».
- Автор отдельно от исполнителя.
- Чтение под изоляцией.
- Две переписи.

**Non-Goals:**
- Момент подачи и его запись — `work-15`; здесь только колонка и правило сравнения.
- Записи приёмки, возврата и правки менеджером — `work-16`. Виды действий дописываются в открытый каталог
  тем change, который их приносит.
- Адрес «ввести за сотрудника» — `work-13` (`/api/work/entry/reports`). Здесь журнал пишет его `enteredFor`.
- Лента журнала на экране — `work-21`.

## Decisions

### Состав PR
Один PR.

### Хранение
- `work.report_journal`: `id`, `report_id`, `change_id`, `seq` (тот же номер, что у `report_changes`),
  `kind`, `author_id`, `author_name`, `executor_employee_id`, `executor_name`, `entered_for`,
  `device_time`, `server_time`, `after_submission`, `changes` JSONB (`[{ line, field, before, after }]`).
  Пометка `IAppendOnly`.
- Колонка `reports.submitted_at` (мгновение, null у черновика) заводится здесь. Писать её будет
  `work-15`; тест этого change ставит её прямо в базе.
- Миграция `ReportJournal`.

### Единственное место записи
`Data/ReportJournal.cs` вызывается из пути сохранения отчёта `work-13`: сравнивает состояние до и после,
строит `changes` и добавляет запись в тот же `SaveChanges`. Виды действий — константы
`ReportJournalKinds` (каталог открыт): `created`, `edited`, `deleted`.

### «Правка после подачи»
- `after_submission = submitted_at != null && device_time > submitted_at`.
- Время устройства — заявление (`MOB-17`): на нём держится только эта пометка, а не отказ.
- Расхождение времён и подозрительный порядок показывает очередь `work-16` нейтральными пометками.

### Изоляция чтения (D4)
- `GET /api/work/reports/{id}/journal` находит отчёт методом с параметром доступа `work-07`: свои,
  стройки или все.
- Черновик отдаётся только сотруднику отчёта (вид «свои») и автору ввода (`reports.entered_by_user_id`,
  `work-13`) (`WORK-54`).
- Чужой, черновик чужого и несуществующий отчёт дают один 404 одним путём кода (`ReportAccess`, как в
  `work-13`).
- Ворота — составная политика «любое из прав» `work-03`.

### Журнал ядра
- Действия `work.report.deleted` и `work.report.edited-after-submission` пишутся через
  `IModuleActivityLog` после `SaveChanges`.
- Сбой здесь — предупреждение в журнал приложения, а не откат: свидетельство уже в журнале отчёта.
- Сторож — `ActivityLogInventoryTests`.

### Сторож и «чем ломается»
- **Сторожа:**
  - `WorkJournalInventoryTests` — второе место записи;
  - `WorkJournalWritePathTests` — `ExecuteUpdate`, `ExecuteDelete`, `FromSql*`, `ExecuteSql*` по
    `report_journal` в исходниках модуля;
  - тест `IAppendOnly` на правку загруженной записи.
- **Чем ломается.** Журнал строкой JSON в отчёте — правка отчёта перезаписывает историю, тест «правка
  записи отвергается» не на чем поставить.

### Зависимости
- `work-13` (путь сохранения, `report_changes`).
- `work-07` (параметр доступа).
- `work-03` (политика «любое из»).
- `work-01` (имя исполнителя через связь).

## Risks / Trade-offs

- [В нарезке у серверного адреса «ввести за сотрудника» (`WORK-31`) не было владельца] → адрес взял
  `work-13` по запросу экранов `work-22`. Тест `enteredFor` идёт через этот адрес.
- [Объём `changes` у большой правки] → в запись кладутся только изменённые поля. Сведения строки — по
  ключам, без копии всего JSON.
