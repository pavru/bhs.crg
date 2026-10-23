# Spec Delta: types-and-tags

## MODIFIED Requirements

### Requirement: Действующие тэги ядра — поля записей
Ядро SHALL объявлять тэги полей `identity` (с номерами), `doc.number`, `doc.date`,
`employee.personnelNumber`, `employee.position`, `employee.employedFrom`, `employee.employedTo`,
`employee.account`, `cert.validUntil`, `ref.workType`, `work.requiresLocation`,
`work.requiresMaterials`, `work.producesId`, `org.inn`, `org.name`, `org.shortName`,
`nomenclature.article`, `nomenclature.unit`, `input.noSuggest`.

#### Scenario: Тэги ядра без модулей
- **WHEN** экземпляр запущен с любым набором модулей
- **THEN** тэги ядра доступны в редакторе схем

#### Scenario: Тэг «не подсказывать» в реестре
- **WHEN** клиент читает `GET /api/tags`
- **THEN** в ответе есть `input.noSuggest` с описанием, владелец — ядро, допустимый вид поля — `string`, кратность — любое число полей

## ADDED Requirements

### Requirement: [CORE-20.4] [TYPE-23.1] Тэг сокращённого названия организации
Тэг `org.shortName` SHALL отмечать сокращённое название организации: строковое поле типа с ролью
`role.organization` или строковую часть составного поля с `org.name`. Одиночный — один на тип. Код MUST
находить сокращённое название только по тэгу; на действующей установке тэг ставится один раз сам —
части «Сокращённое» составного поля названия, если её никто не отметил.

#### Scenario: Наименование составное, как у заказчика
- **WHEN** у типа «Организация» поле «Наименование» с `org.name` — составное с частями «Полное» и
  «Сокращённое», и экземпляр обновляется
- **THEN** часть «Сокращённое» получает тэг `org.shortName`, и повторный старт ничего не меняет

#### Scenario: Сокращённое — отдельное поле
- **WHEN** администратор ставит `org.shortName` на строковое поле «Краткое название» типа организации
- **THEN** сохранение схемы проходит, и код берёт сокращённое название из этого поля

#### Scenario: Два поля с тэгом
- **WHEN** администратор ставит `org.shortName` на два поля одного типа
- **THEN** сохранение схемы отклоняется
