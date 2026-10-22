# Spec Delta: types-and-tags

## MODIFIED Requirements

### Requirement: Действующие тэги ядра — поля записей
Ядро SHALL объявлять тэги полей `identity` (с номерами), `doc.number`, `doc.date`,
`employee.personnelNumber`, `employee.position`, `employee.employedFrom`, `employee.employedTo`,
`employee.account`, `cert.validUntil`, `ref.workType`, `work.requiresLocation`,
`work.requiresMaterials`, `work.producesId`, `org.inn`, `org.name`, `nomenclature.article`,
`nomenclature.unit`, `input.noSuggest`.

#### Scenario: Тэги ядра без модулей
- **WHEN** экземпляр запущен с любым набором модулей
- **THEN** тэги ядра доступны в редакторе схем

#### Scenario: Тэг «не подсказывать» в реестре
- **WHEN** клиент читает `GET /api/tags`
- **THEN** в ответе есть `input.noSuggest` с описанием, владелец — ядро, допустимый вид поля — `string`, кратность — любое число полей

## ADDED Requirements

### Requirement: [TYPE-2] Правило «поле подсказывается»
Подсказывается ли поле значениями, введёнными ранее, SHALL решать одно правило ядра по полю схемы.
Правило MUST возвращать код причины, а не да/нет: «подсказывается» или `type`, `computed`, `locked`,
`identity`, `inn`, `docNumber`, `personnelNumber`, `manual`. Подсказывается только вид `string`.
Умолчание MUST вычисляться, а не храниться: свойства «включить подсказки» у поля нет.

#### Scenario: Обычная строка
- **WHEN** поле вида `string` без тэгов, не вычисляемое и не запертое
- **THEN** правило отвечает «подсказывается»

#### Scenario: Многострочный текст и пользовательский вид
- **WHEN** поле вида `text` или `primitive`
- **THEN** правило отвечает `type`

#### Scenario: Тэг однозначности
- **WHEN** строковое поле несёт тэг `org.inn`
- **THEN** правило отвечает `inn`, а не «подсказывается»

#### Scenario: Выключено вручную
- **WHEN** строковое поле несёт тэг `input.noSuggest`
- **THEN** правило отвечает `manual`

#### Scenario: Вычисляемое и запертое поле
- **WHEN** строковое поле вычисляемое или заперто модулем
- **THEN** правило отвечает `computed` или `locked` соответственно

### Requirement: [TYPE-2] Зеркало правила на клиенте и общий набор случаев
Клиент SHALL держать зеркало правила «поле подсказывается» с теми же кодами. Обе реализации MUST
проверяться одним общим набором случаев — случай на каждый код и на порядок проверок.

#### Scenario: Реализации разошлись
- **WHEN** серверное правило и его зеркало на клиенте дают разный код на случае из общего набора
- **THEN** тест отставшей стороны краснеет с названием случая

#### Scenario: Тэг переживает сохранение схемы
- **WHEN** схему с полем, несущим `input.noSuggest`, разбирают и сохраняют в редакторе типа
- **THEN** тэг остаётся на поле
