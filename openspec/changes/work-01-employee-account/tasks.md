# Tasks: связь «учётная запись → сотрудник» и экран сотрудников

## 1. PR 1: связь (сервер)

- [ ] 1.1 Сущность `EmployeeAccount` в `BHS.CRG.Domain/Catalog/` и её конфигурация: ключ по сотруднику,
  частичный уникальный индекс по `UserId`, `SetNull` на `AspNetUsers`, снимок `AccountName`
  [WORK-47] [CORE-15]. Миграция `dotnet ef migrations add EmployeeAccounts --project src/server/BHS.CRG.Infrastructure --startup-project src/server/BHS.CRG.Api --context AppDbContext`.
  Проверка — на пересозданной базе старт применяет миграцию, `dotnet build BHS.CRG.slnx` без ошибок
- [ ] 1.2 `PUT` и `DELETE /api/employees/{id}/account` под `core.employees.edit`: `If-Match` по `version`
  карточки (`RecordIfMatch`), связывание трогает `UpdatedAt` объекта той же транзакцией; отказ для
  архивного сотрудника; повторное отвязывание безобидно [WORK-47] [AUTH-8] [AUTH-9]. Проверка — новый
  `EmployeeAccountLinkTests`: без заголовка — 400, устаревшая версия — 409, без права — 403,
  `EndpointGateInventoryTests` видит оба адреса под правом
- [ ] 1.3 Сторож уникальности: вторая связь той же учётной записи — 409 с именем занятого сотрудника;
  связь занятого сотрудника — 409 с именем текущей учётной записи [WORK-47] [CORE-15]. Проверка — тест в
  `EmployeeAccountLinkTests` краснеет, если снять уникальный индекс в конфигурации
- [ ] 1.4 Коды `core.employee.account.linked` и `core.employee.account.unlinked` в `ActivityActions.cs`.
  Запись после удавшегося действия, прежнее значение — имя учётной записи [CORE-28] [WORK-47]. Проверка —
  тест: связал и отвязал — в `/api/activity` две записи с автором
- [ ] 1.5 `GET /api/employees/account-candidates?q=` под `core.employees.edit`: имя, логин, `linkedTo`, без
  ролей и почты [WORK-47]. Проверка — тест: занятая запись приходит с `linkedTo`, пользователь с
  `core.employees.read` получает 403
- [ ] 1.6 Носитель `account: { state, name }` в выдаче `GET /api/employees` и `/{id}`. Удаление учётной
  записи даёт `deleted` с именем [WORK-47] [CORE-7]. Проверка — тест: удалил пользователя через
  `DELETE /api/users/{id}` — сотрудник цел, `state: deleted`; отвязал — `none`
- [ ] 1.7 `employee: { id, name, archived } | null` и `employeeReason` (`not-linked`, `directory-missing`) в
  `GET /api/account/access` [AUTH-14] [WORK-47]. Проверка — `AccountAccessTests` дополнен тремя случаями:
  связан, не связан, тип «Сотрудник» не заведён
- [ ] 1.8 Исключение `EmployeeAccount` в `BackupManifestCoverageTests.cs` с причиной «связь адресует учётную
  запись, а учётные записи копия не переносит» [CORE-7] [CORE-26]. Проверка —
  `BackupManifestCoverageTests` зелёный и краснеет без строки исключения
- [ ] 1.9 `docs/ADMIN_GUIDE.md`: глава о связывании сотрудника с учётной записью, отвязывании, состоянии
  «учётная запись удалена» и потере связей при восстановлении копии на новый экземпляр. Перестроить
  содержание: `cd docs/tools && npm run toc`. Проверка — проверка содержания в «Frontend» зелёная

## 2. PR 2: экран сотрудников (клиент), после выбора макета Р1

- [ ] 2.1 Вписать ссылку на выбранный владельцем макет Р1 в `design.md` этого change [WORK-27]. Проверка —
  ссылка в разделе «Макеты»
- [ ] 2.2 `GET /api/employees` при незаведённом типе отвечает 409 `directory-missing`, а не пустым списком.
  Перед правкой — `grep` потребителей адреса в `src/client/src` и `src/server/BHS.CRG.Api/Mcp`
  [CORE-7]. Проверка — тест в `EmployeeDirectoryTests` на базе без «Персоны»
- [ ] 2.3 Раздел ядра `employees` в `BHS.CRG.Modules/CoreSections.cs` и пункт «Сотрудники» в
  `shared/ui/navConfig.ts` (`permission: 'core.employees.read'`, `rail: 'data'`, `section: 'employees'`)
  [WORK-27] [AUTH-16]. Проверка — `CoreSectionsTests` зелёный; vitest `navAccess.test.ts`: пункт скрыт без права
- [ ] 2.4 `features/employees/EmployeesPage.tsx` — list-detail на `ListDetailShell`. Блок «Учётная запись»
  строится по `account.state`. Кнопки «Связать» и «Отвязать» — только при `core.employees.edit`;
  диалог кандидатов, занятые не выбираются. Отказ `directory-missing` даёт пустое состояние
  «справочник не заведён» [WORK-27] [WORK-47]. Проверка — vitest на разбор `account.state` и
  `npm run ui:ratchet` без роста
- [ ] 2.5 Живой прогон: администратор связывает и отвязывает сотрудника, «Менеджер проекта» видит
  состояние без кнопок — проверки в `e2e/pages-smoke.mjs` и `e2e/limited-smoke.mjs` [WORK-27]. Проверка —
  `MSYS_NO_PATHCONV=1 npm run test:e2e:pages` и `npm run test:e2e:limited` зелёные
- [ ] 2.6 `docs/USER_GUIDE.md`: раздел «Сотрудники» в главе «Данные». Перестроить содержание:
  `cd docs/tools && npm run toc` [WORK-27]. Проверка — проверка содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~EmployeeAccount|FullyQualifiedName~EmployeeDirectory|FullyQualifiedName~AccountAccess|FullyQualifiedName~BackupManifestCoverage|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~CoreSections"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
