# Spec Delta: costs

## ADDED Requirements

### Requirement: [COST-17] [COST-18] [CORE-34] Контракт «Материалы на объекте»
Модуль SHALL отдавать другим модулям контрактом «Материалы на объекте» перечень номенклатуры,
отпущенной на названную стройку проведёнными накладными. По `costs.materials.read` — позиции с единицей,
количеством, первой и последней датой и числом не вошедших строк без номенклатуры; по
`costs.materials.hint` — только позиции, без количеств, дат и счётчиков. Сумм контракт MUST NOT отдавать.

#### Scenario: Полный ответ
- **WHEN** контракт спрашивает пользователь с `costs.materials.read` по стройке А, где проведены две накладные с кабелем
- **THEN** ответ несёт позицию кабеля с количеством и датами, а также число строк без номенклатуры

#### Scenario: Подсказка монтажнику
- **WHEN** контракт спрашивает пользователь только с `costs.materials.hint` по той же стройке
- **THEN** ответ несёт позицию кабеля без количества, дат и счётчиков

#### Scenario: Черновик накладной
- **WHEN** на стройку есть только черновик накладной
- **THEN** его строк в ответе нет ни при одном праве

#### Scenario: Ни одного из двух прав
- **WHEN** контракт спрашивает пользователь без `costs.materials.read` и `costs.materials.hint`
- **THEN** ответ «скрыто правами», данных в нём нет

### Requirement: [WORK-48] [AUTH-8.1] Права контракта модуль не открывают
`costs.materials.read` и `costs.materials.hint` SHALL быть правами контракта: они MUST NOT открывать ни
ворота модуля `costs`, ни его навигацию. «Монтажник» SHALL получать `costs.materials.hint`; «Инженер
ИД» и «Менеджер проекта» — `costs.materials.read`.

#### Scenario: Монтажник
- **WHEN** пользователь с ролью «Монтажник» читает `GET /api/account/access`
- **THEN** у него есть `costs.materials.hint`, а модуля `costs` в доступе нет
- **AND** любой адрес `/api/costs/*` отвечает ему 403

#### Scenario: Инженер ИД
- **WHEN** пользователь с ролью «Инженер ИД» читает `GET /api/account/access`
- **THEN** у него есть `costs.materials.read`, а пространства «Счета и накладные» в его навигации нет

#### Scenario: Состав доступа по ролям
- **WHEN** экземпляр стартует с модулями `costs` и `work`
- **THEN** состав `/api/account/access` по каждой системной роли совпадает с ожидаемым

## MODIFIED Requirements

### Requirement: [COST-28] [COST-29] Права модуля
Модуль SHALL объявлять права: `costs.invoice.read`, `costs.invoice.edit`, `costs.invoice.correct`,
`costs.organization.create`, `costs.invoice.pay`, `costs.waybill.read`, `costs.waybill.edit`,
`costs.allocation.edit`, `costs.report.read`, `costs.articles.edit` — каждое с пометкой `ReadAllMark`, —
и права контракта `costs.materials.read` и `costs.materials.hint`, которые модуль не открывают.
Право на накладные MUST NOT давать права на счета.

#### Scenario: Роли по умолчанию
- **WHEN** экземпляр устанавливается с модулем `costs`
- **THEN** системные роли «Снабженец» и «Бухгалтер» получают права модуля по своему назначению

#### Scenario: Только накладные
- **WHEN** у пользователя есть `costs.waybill.read`, но нет `costs.invoice.read`
- **THEN** адреса счетов отвечают ему отказом

#### Scenario: Только право контракта
- **WHEN** у пользователя из прав `costs` есть только `costs.materials.read`
- **THEN** адреса `/api/costs/*`, в том числе `GET /api/costs/materials`, отвечают ему отказом
