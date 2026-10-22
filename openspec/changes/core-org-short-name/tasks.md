# Tasks: организация в модулях — сокращённым названием, если оно есть

Один PR с «Closes #1473», метка `version:minor` (новый тэг ядра и правило показа).

## 0. До кода

- [x] 0.1 (22.10.2026: В1 — Б, полное второй строкой в выборе; В2 — отбор по обоим) Решение владельца — до кода: В1 (где видно полное название) и В2 (отбор по сокращённому или по
  обоим). Проверка — ответ записан в `design.md` (D4, D5) и в issue
- [ ] 0.2 Сверка на копии базы заказчика (агрегатами, без названий): вид поля с `org.name`, код его части
  с сокращённым названием, сколько организаций его заполнили, сколько записей хранят только `_baseRef`
  [CORE-20.4]. Проверка — числа в описании PR; если часть называется не «Сокращённое» — правка D2 до кода

## 1. Тэг

- [ ] 1.1 `FunctionalTag.OrgShortName = "org.shortName"`, запись в `TagRegistry` (поле, `string` / `text`,
  одиночный, группа «Организации», роль `role.organization`); допустимость на части составного поля
  `org.name` носителя роли [CORE-20.4] [TYPE-23.1]. Проверка — тест сохранения схемы: часть составного
  принимается, два поля с тэгом — отказ; `GET /api/tags` отдаёт тэг с описанием
- [ ] 1.2 Правило `DirectoryRoleDefaults`: часть «Сокращённое» составного поля `org.name` получает тэг,
  если его нет ни на одном поле [CORE-20.4]. Проверка — `OrganizationShortNameTests`: тэг ставится, второй
  прогон ничего не меняет, отмеченное вручную поле не трогается
- [ ] 1.3 `Modules.Costs/CostsDirectories.cs`: `org.shortName` — необязательная нужда модуля (без него
  модуль работает) [COST-23]. Проверка — `ModuleNeedsTests`, `ModuleCoverageTests` зелёные

## 2. Порт ядра

- [ ] 2.1 Порт справочника: `DisplayName` записей роли `role.organization` — сокращённое, если заполнено
  (с наследованием `_baseRef`), иначе название записи; `FullName` — название записи; во всех чтениях
  порта (`ModuleCatalogPort`, `ModuleCatalogIntakePort`, `ModuleOwnCatalogPort`) [CORE-6] [CORE-20.4].
  Проверка — `OrganizationShortNameTests`: сокращённое, пустое, унаследованное, без тэга
- [ ] 2.2 Перепись мест `costs`, показывающих организацию: все берут название из порта [COST-6].
  Проверка — тест-перепись зелёный; список мест — `design.md`, «Что выяснилось сверкой»

## 3. Экраны `costs`

- [ ] 3.1 `GET /api/costs/organizations` отдаёт полное название рядом с названием для показа; выбор
  поставщика и плательщика в `InvoiceForm` показывает его по ответу В1 [COST-6]. Проверка — тест адреса;
  живой прогон `invoices` зелёный
- [ ] 3.2 Реестр счетов: колонка и отбор по названию поставщика — по D5 и ответу В2 [COST-20.1] [COST-6].
  Проверка — тест `InvoiceTableRows`: отбор находит счёт по сокращённому названию
- [ ] 3.3 `docs/ADMIN_GUIDE.md`: тэг «Сокращённое название организации», где он стоит и что ставится сам
  [CORE-20.4]. Проверка — ревью PR; новая глава — с `npm run toc` в `docs/tools`

## 4. Верификация

- [ ] 4.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~OrganizationShortName|FullyQualifiedName~DirectoryRole|FullyQualifiedName~ModuleNeeds|FullyQualifiedName~ModuleCoverage|FullyQualifiedName~InvoiceTable|FullyQualifiedName~Organization|FullyQualifiedName~CatalogIntake"` — зелёный
- [ ] 4.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 4.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 4.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
- [ ] 4.5 Перед архивацией: требование «Действующие тэги ядра — поля записей» правит и `suggest-01-rule` (тэг `input.noSuggest`). Кто архивируется вторым, сверяет MODIFIED с действующей спекой и переносит оба тэга, иначе архивация затрёт первый
