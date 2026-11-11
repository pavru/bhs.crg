# Spec Delta: auth

## ADDED Requirements

### Requirement: [AUTH-14] [WORK-47] Сотрудник вошедшего в ответе доступа
`GET /api/account/access` SHALL нести поле `employee` — сотрудника, связанного с учётной записью
вошедшего: `{ id, name, archived }`. Если сотрудника нет, поле MUST быть `null`, а рядом
`employeeReason` называет причину: `not-linked` или `directory-missing`. Клиент не выводит отсутствие
сотрудника из других полей ответа.

#### Scenario: Монтажник со связью
- **WHEN** вошедший связан с сотрудником «Иванов И. И.»
- **THEN** ответ несёт `employee: { id, name: "Иванов И. И.", archived: false }` и `employeeReason: null`

#### Scenario: Учётная запись без сотрудника
- **WHEN** вошедший ни с кем не связан
- **THEN** `employee` — `null`, `employeeReason` — `not-linked`

#### Scenario: Справочник не заведён
- **WHEN** на экземпляре нет типа «Сотрудник»
- **THEN** `employee` — `null`, `employeeReason` — `directory-missing`

#### Scenario: Связь сняли во время работы
- **WHEN** администратор отвязал учётную запись, пока пользователь работает
- **THEN** следующий запрос `/api/account/access` приходит с `employee: null` без перелогина
