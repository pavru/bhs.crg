# Tasks: принудительное удаление позиций перечня работ

Один PR с меткой `version:minor`, в описании — «Closes #1244». Условие старта — слиты `plan-03` и `plan-07`
(позиции заводятся импортом сметы).

## 0. До кода

- [x] 0.1 (22.10.2026: (а) — вход в отказе удаления вида работы и единицы) Решение владельца — где вход (`design.md`, Open Questions, В-1; рекомендация — отказ удаления
  вида работы и единицы); ответ вписан в design, D5 и сценарии дельты сверены [CORE-11] [CORE-34.2].
  Проверка — в design нет вопроса без ответа
- [ ] 0.2 Разбор с Дизайнером и три макета в Claude Design (предложение в отказе, диалог ввода числа
  позиций с числом ссылок рядом; образец — `PurgeRecordDialog`); выбор владельца записан ссылкой в design
  [CORE-34.2]. Проверка — ссылка на макет и вариант в design.md

## 1. Сервер

- [ ] 1.1 Команда принудительного удаления позиций записи (вид работы или единица) рядом с
  `Application/Documents/WorkPlanItems.cs`: держатели всех позиций — `RecordHoldings`, правило
  `Release` (не включён ни один, скан прочитан); число позиций сверяется в транзакции; удаление целиком
  [CORE-11] [CORE-34.2] [AUTH-19]. Проверка — `WorkPlanItemPurgeTests`: держит включённый — отказ;
  неверное число — отказ со свежим предложением; позиции без держателей — ссылок 0; непрочитанная
  колонка — отказ
- [ ] 1.2 Адрес `POST /api/common-data/{id}/plan-items/purge` в группе `core.catalog.purge`; `If-Match` —
  названное исключение с причиной в доккомментарии адреса [AUTH-8] [CORE-34.2]. Проверка —
  `WorkPlanItemPurgeTests`: 403 без права; `EndpointGateInventoryTests` зелёный
- [ ] 1.3 Событие журнала `core.workplanitems.purged` с записью, числом позиций и ссылок, модулями
  [CORE-34.2]. Проверка — `WorkPlanItemPurgeTests` читает событие; `ActivityLogInventoryTests` зелёный
- [ ] 1.4 Отказ удаления вида работы и единицы (`CommonDataHandlers.cs`, дверь `plan-03`) — по решению
  0.1: предложение полями отказа (число позиций, ссылок, модули), без слов «из интерфейса пока нельзя»
  [CORE-11]. Проверка — тест: после принудительного удаления позиций вид работы удаляется обычным путём

## 2. Клиент

- [ ] 2.1 Предложение в отказе и диалог по выбранному макету (0.2) — ввод числа позиций, число ссылок
  рядом, отказ называет причину [CORE-34.2]. Проверка — vitest на логику диалога; живой прогон: удаление
  вида работы, занятого перечнем выключенного модуля
- [ ] 2.2 `docs/ADMIN_GUIDE.md` — удаление вида работы или единицы, занятых перечнем выключенного модуля;
  содержание — `npm run toc` [CORE-34.2]. Проверка — проверка «Frontend» (оглавление) зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~WorkPlanItem|FullyQualifiedName~RecordPurge|FullyQualifiedName~OccupiedRecordDelete|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ActivityLogInventory"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
