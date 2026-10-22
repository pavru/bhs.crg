# Proposal: связь «учётная запись → сотрудник» и экран сотрудников

Дочерний change зонтика `work-00-epic`, общая архитектура — `../work-00-epic/design.md` (D6, D15, D17).

## Why

Отчёт монтажника принадлежит сотруднику, а входит в систему учётная запись. Сегодня связи между ними
нет: поле `УчётнаяЗапись` у типа «Сотрудник» заведено запертой строкой, которую некому заполнить
(`CoreRecordTypes.cs:65-92`). «Свои» отчёты (`WORK-47`), назначения (`work-11`) и бригада (`work-18`)
опираются на эту связь. Строка в JSON не держит уникальность: одна учётная запись оказалась бы
сотрудником дважды (`CORE-15`). Change ранний (Р-5): этап 3 его не ждёт.

## What Changes

- **PR 1 — связь.**
  - Таблица ядра «учётная запись → сотрудник», строго один к одному. Другого сотрудника можно связать
    только после «отвязать».
  - Связывание и отвязывание — адреса `/api/employees/{id}/account` под `core.employees.edit`, с
    `If-Match`; событие пишется в журнал действий.
  - Узкий адрес кандидатов: имя, логин, «уже связана с X».
  - Носитель у сотрудника: `account: { state: linked | none | deleted, name }`. Удалённая учётная
    запись даёт `deleted`, а не `none`.
  - Поле `employee` в `/api/account/access` — сотрудник вошедшего или `null` с причиной.
  - Связь в резервную копию не едет, как и учётные записи (`CORE-7`). Исключение названо с причиной.
- **PR 2 — экран.** Раздел ядра `employees` в рейле «Данные»: list-detail сотрудников со связью.
  Код — после выбора макета раунда Р1.
- Поле `УчётнаяЗапись` в «Общих данных» остаётся запертым кодом (`CORE-20.2`). Источник связи —
  таблица, а не JSON.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `core`: связь учётной записи с сотрудником, адреса связывания, носитель состояния, исключение из копии,
  экран сотрудников.
- `auth`: поле `employee` в ответе `/api/account/access`.

## Impact

- **Сервер:**
  - новая сущность и конфигурация связи в `BHS.CRG.Domain` и `BHS.CRG.Infrastructure/Persistence`;
  - миграция ядра (`--context AppDbContext`);
  - правки `Api/Endpoints/Core/EmployeeEndpoints.cs` и `Api/Endpoints/Account/AccountEndpoints.cs`;
  - коды действий в `Application/Activity/ActivityActions.cs`;
  - исключение в `Tests/Integration/BackupManifestCoverageTests.cs`.
- **Клиент:** новая страница `features/employees/*`, пункт в `shared/ui/navConfig.ts`, раздел
  `employees` в `BHS.CRG.Modules/CoreSections.cs`.
- **Документы:** `docs/ADMIN_GUIDE.md` (связывание), `docs/USER_GUIDE.md` (экран), `npm run toc`.
- **Потребители:** `work-11` (порт сотрудника), `work-13` («свои» отчёты), `work-18` (бригада).
