# Design: назначения монтажника и менеджера

Общая архитектура — `../work-00-epic/design.md` (D4, D5, D8, D15). Здесь только решения этого change.

## Context

- Модуль, схема `work` и права первой двери уже есть (`work-10`). Связь «учётная запись → сотрудник» —
  `work-01`, параметр доступа — `work-07`.
- Порт строек отдаёт `ModuleConstruction(Id, Name, Sections)` без пояса
  (`src/server/BHS.CRG.Modules/Ports/IModuleConstructions.cs:7`). При этом пояс у стройки в домене уже
  есть: `Construction.TimeZoneId` (`src/server/BHS.CRG.Domain/Documents/Construction.cs:27`).
- `IModuleClock` знает только «сегодня компании» и сам называет «сегодня на стройке» другим методом
  (`src/server/BHS.CRG.Modules/Ports/IModuleClock.cs:15-17`). Реализация —
  `src/server/BHS.CRG.Api/Modules/Ports/ModuleClockPort.cs`, через `TimeProvider`.
- Сотрудник доступен модулю только строкой JSON через `IModuleCatalog`, и только по роли справочника. Роли
  «сотрудник» нет: в `FunctionalTag.cs:149,152` их две — организация и номенклатура. Поля сотрудника
  объявлены кодом, с тэгами (`src/server/BHS.CRG.Application/Documents/CoreRecordTypes.cs:65-92`).

## Goals / Non-Goals

**Goals:**
- Назначения с правом по стройке.
- Свои стройки монтажника на дату.
- Разрешение строек менеджера для `work-07`.
- Три порта ядра.

**Non-Goals:**
- Экран назначений — `work-12` (макеты Р2).
- Порт вида работ и вариантов — `work-06`.
- Предлагаемая пара «дата · смена» — `work-15`: она зависит от правила окна подачи.
- Адрес стройки в «моих стройках»: колонки адреса у стройки нет (`Construction.cs:7-37`), он живёт в её
  профиле. Нужен он телефону (`WORK-24`), то есть этапу 5.

## Decisions

### Состав PR
Один PR: порты ядра и их первый потребитель едут вместе (правило 6 нарезки).

### Хранение
- Таблица `work.assignments`: `id`, `employee_id`, `construction_id`, `kind` (`installer` | `manager`),
  `date_from`, `date_to` (null только у `manager` — проверка в базе), `xmin` как версия.
- Индекс `(construction_id, kind, date_from)`.
- Пересечение проверяет сервер под блокировкой строк сотрудника на стройке. Исключающее ограничение
  `EXCLUDE USING gist` отклонено: отказ базы не называет пересекающееся назначение, а назвать его требует
  спецификация.

### Право по стройке
- Одно место проверки: `Access/AssignmentGate.cs` отвечает на вопрос «может ли спрашивающий назначать на
  стройке S» и зовётся каждым пишущим адресом.
- `work.sites.all` проверяется через `IModuleUser.HasAsync`.
- Сторож — тест «назначение на чужую стройку — 403» по каждому пишущему адресу. Тест-перепись адресов
  `/api/work/assignments*` проверяет, что каждый пишущий адрес зовёт ворота стройки.

### If-Match и названное исключение
- `PUT` и `DELETE` читают версию через `SeenVersion`, по образцу
  `src/server/BHS.CRG.Modules.Costs/Endpoints/InvoiceDesk.cs:242`.
- **Исключение:** `POST /api/work/assignments` и `POST /api/work/assignments/bulk` версии не называют.
  Заводят новую запись, и прежней версии у неё нет. От двойного заведения защищает проверка пересечений:
  повтор того же назначения — 409 с названием уже заведённого.

### Стройки менеджера (для `work-07`)
`Access/ManagerSites.cs` выдаёт набор строек, где у сотрудника учётной записи есть назначение «менеджер»,
действующее на «сегодня» по поясу каждой стройки. Кэша нет: снятие обязано действовать на следующем
запросе. Строек — десятки, назначений менеджера — единицы.

### Порты ядра
- `ModuleConstruction` получает `TimeZoneId` (действующий, не null) и `TimeZoneInherited`. Пояс компании
  порт берёт у `IAppSettingsStore.GetCompanyTimeZoneAsync`. Потребители в `costs` создают запись только
  через порт (`ModuleConstructionsPort.cs:27`), поэтому поведение `costs` не меняется.
- `IModuleClock` получает два метода:
  - `TodayOnSiteAsync(Guid constructionId)`;
  - `SiteDateOf(Guid constructionId, DateTimeOffset instant)` — дата стройки для мгновения. Нужна
    `work-15`, вводится сейчас, чтобы правило суток жило в одном месте.

  Незнакомая стройка — `null`.
- Новый `BHS.CRG.Modules/Ports/IModuleEmployees.cs`:
  - `ListAsync(RecordsFor)` и `GetManyAsync(ids)`;
  - запись `ModuleEmployee(Id, FullName, PersonnelNumber, EmployedFrom, EmployedTo, Archived, Account)`,
    где `Account` — `{ State: linked | none | deleted, UserId?, Name? }` из таблицы связи `work-01`.

  Тип ищется по коду `CoreRecordTypes.EmployeeCode`, поля — по тэгам `employee.*`. Справочника нет —
  ответ `null`, как у `IModuleCatalog.ListAsync`. Реализация — `BHS.CRG.Api/Modules/Ports/ModuleEmployeesPort.cs`.

### Носители (D15)
- У стройки в чтении назначений — `canAssign` и `reason`, у назначения — `canChange` и `canRemove` с
  причиной. Признаки считает тот же `AssignmentGate`, что отказывает пишущему адресу, поэтому кнопка и
  отказ не расходятся (запрос экрана `work-12`).
- У кандидата — `eligible` и `reason`.
- У «моих строек» — `today` и `boundary: { text, count, reason }`; `reason` — `null` или `not-assigned`.
- Клиент отсутствие не вычисляет.

### Ссылки и журнал
- `WorkReferences.cs` объявляет держащими `assignments.employee_id` (`ReferenceTarget.Record`, «назначения»)
  и `assignments.construction_id` (`ReferenceTarget.Construction`).
- Действия `work.assignment.created`, `.changed`, `.removed` объявлены в каталоге модуля и пишутся через
  `IModuleActivityLog`. Сторож — `ActivityLogInventoryTests`.

### Чем ломается
- Право проверяется без стройки — тест «чужая стройка — 403» краснеет.
- «Сегодня» считается по поясу компании — тест Владивостока краснеет.
- Добавление метода в `IModuleClock` роняет сборку тестового двойника
  `src/server/BHS.CRG.Tests/Integration/InvoiceDueTableTests.cs:33`. Двойник правится тем же PR.

### Зависимости
- `work-10` (модуль), `work-01` (связь сотрудника), `work-07` (тип параметра доступа: этот change его
  наполняет).
- От этапа 3 не зависит.

## Risks / Trade-offs

- [Менеджер снял сам себя и потерял стройку] → снять последнее назначение «менеджер» на стройке может
  только владелец `work.sites.all`. Иначе 409 «на стройке не останется менеджера». Правило — решение
  сессии, а не владельца: при разборе Р2 подтвердить.
- [Нарезка относит порт сотрудника к `work-06`] → путь `10 → 02 → 07 → 11 → 06` ставит этот change раньше.
  Порт сотрудника строится здесь, `work-06` добавляет вид работы и варианты тем же приёмом.
