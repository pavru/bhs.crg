# Tasks: исправление накладной закрытого периода

Функциональный change: PR с меткой `version:minor`, в описании — «Closes #1340». Условие старта —
`plan-12-record-versions` слит (или см. design, Risks).

## 0. До кода

- [x] 0.1 (22.10.2026: В-1 (а), В-2 (б), В-3 (а) — рекомендации приняты, дельта под них) Решение владельца по В-1 (второй взгляд), В-2 (право), В-3 (запирает ли версия) — `design.md`,
  Open Questions; ответ вписан в design и, если отличается от рекомендации, дельта `specs/costs/spec.md`
  переписана [COST-16] [COST-29] [CORE-36]. Проверка — в design нет вопросов без ответа
- [x] 0.2 (26.10.2026: вариант 1a с поправками, D10) Разбор с Дизайнером и три макета в Claude Design: исправление в форме накладной, список версий в
  карточке, пометка в списке (образец — исправление счёта: `CorrectionBar`, `CorrectionHistory`); выбор
  владельца записан ссылкой в design [COST-16]. Проверка — ссылка на макет и выбранный вариант в design.md

## 1. Версии накладной

- [x] 1.1 `Modules.Costs/Data/WaybillVersion.cs` — наследник `ModuleRecordVersion`; `Waybill.CurrentVersion`;
  конфигурация в `CostsDbContext`, миграция `dotnet ef migrations add WaybillVersions --project
  src/server/BHS.CRG.Modules.Costs --context CostsDbContext` [CORE-36]. Проверка — старт на пересозданной
  базе применяет миграцию; правка и удаление строки версии отвергаются (`IAppendOnly`)
- [x] 1.2 `RecordVersionInventoryTests`: словарь для `WaybillVersion` — запись только в
  `WaybillCorrection.cs`, чтение истории — только адрес версий; `IssuedMaterials.cs` и
  `WaybillClosingLines.cs` к версиям не обращаются [CORE-36]. Проверка — тест красный на обращении к
  версиям из `IssuedMaterials.cs`

## 2. Исправление

- [x] 2.1 Право `costs.waybill.correct` (решение 0.1; зависит от
  `costs.waybill.edit`, `ReadAllMark.Out`) в `CostsModule.cs`; составы ролей [COST-28] [COST-29]
  [AUTH-8]. Проверка — тест состава прав по ролям; «Читать всё» права не включает
- [x] 2.2 `WaybillDesk`: путь исправления — замок записи, чтение, «нужно ли исправление» и право раньше
  `If-Match`, запись версии и правка одной транзакцией; учётная дата не двигается, кроме смены стройки
  (`WaybillLock.AccountedOn`) [COST-16] [CORE-36]. Проверка — `WaybillCorrectionTests`: смена стройки
  пересчитывает учётную дату, смена строк — нет
- [x] 2.3 `Endpoints/WaybillCorrection.cs`: `POST /api/costs/waybills/{id}/correction` (причина обязательна,
  `If-Match`, право, ворота модуля), `GET /api/costs/waybills/{id}/versions` под `costs.waybill.read`;
  журнал `WaybillActions.Corrected` [COST-16] [CORE-36] [AUTH-8] [AUTH-9]. Проверка —
  `WaybillCorrectionTests`: 403 без права, 400 без причины, 400 без `If-Match`, 409 устаревшая, 409
  «исправление не нужно» у открытого периода, отказ выключенного модуля, версия пишется и читается;
  адрес вписан в `VersionAfterNeed` у переписи пишущих адресов накладной (`WaybillTests.ClosedPeriod.cs`:
  `InvoiceStaleFormTests` перебирает только адреса счёта); `EndpointGateInventoryTests` зелёный.
  Тесты — `WaybillTests.Correction.cs` (часть класса `WaybillTests`: общие помощники накладной)
- [x] 2.4 Слова: отказ `WaybillDesk` — выход «исправить с причиной»; примечание диалога закрытия
  (`WaybillClosingLines`) — «только исправлением с причиной» [COST-16]. Проверка — тест отказа сверяет
  выход; тест диалога закрытия — новое примечание
- [x] 2.5 Строка списка накладных — `correctedAfterClosing` (день и причина последнего исправления) [COST-16].
  Проверка — тест списка: исправленная помечена, неисправленная — нет
- [x] 2.6 «Материалы на объекте» после исправления — новое количество, без удвоения [COST-17] [CORE-36].
  Проверка — сценарий в `WaybillCorrectionTests`
- [x] 2.7 Удаление исправленной накладной (D8): черновик после отмены закрытия с `CurrentVersion > 1` — 409
  «история исправлений не удаляется», без обращения к версиям [CORE-36]. Проверка — сценарий в
  `WaybillTests.Correction.cs`

## 3. Клиент

- [ ] 3.1 Форма накладной: исправление запертой накладной по выбранному макету (0.2) — причина, всё
  содержимое, отказы называют причину [COST-16]. Проверка — vitest на логику черновика исправления;
  живой прогон `costs-smoke.mjs`: исправление запертой накладной, версия видна
- [ ] 3.2 Карточка — список версий с причинами; список — пометка «исправлена после закрытия» [COST-16]
  [CORE-36]. Проверка — vitest; живой прогон `costs-smoke.mjs`

## 4. Документы

- [ ] 4.1 `docs/USER_GUIDE.md` — исправление накладной закрытого периода; содержание — `npm run toc` в
  `docs/tools`; `docs/tz/55-счета-и-накладные.md`, `COST-16` — «накладная исправляется отдельной задачей
  (#1340)» заменено построенным [COST-16]. Проверка — проверка «Frontend» (оглавление) зелёная, ревью PR

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Waybill|FullyQualifiedName~RecordVersionInventory|FullyQualifiedName~InvoiceWritePath|FullyQualifiedName~InvoiceStaleForm|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~ModuleBoundary"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
