# Tasks: печать записи модуля и форма КП

## 0. Условие

- [ ] 0.1 Ответ владельца на В-13 (право на шаблоны типов модулей) вписан в `docs/tz/50-планирование.md`;
  при другом ответе поправить ворота в 1.4 и пометку в `design.md`. Проверка — в `design.md` нет «решение В-13»

## 1. PR 1: ядро — печать записи модуля

- [ ] 1.1 `BHS.CRG.Modules/Printing/ModulePrintForm.cs`, `IModulePrintContext.cs`; член
  `IAppModule.PrintForms` с пустым умолчанием [CORE-21] [CORE-14]. Проверка — `ModuleBoundaryTests` зелёный
- [ ] 1.2 Проверка при старте: тип формы есть в `RecordTypes` того же модуля, поставщик контекста ровно один
  [CORE-21]. Проверка — `ModulePrintFormCatalogTests` на пробном модуле: без контекста и с двумя — отказ
  старта с названием формы
- [ ] 1.3 `PrintRecordCommand` над `IDocumentGenerator`; общая сборка блоков типов, библиотеки и ассетов
  вынесена из `GenerateDocumentHandler` [CORE-21]. Проверка — тесты генерации документов `id` зелёные без
  правок; `RecordPrintTests`: шаблона нет — 409 с причиной
- [ ] 1.4 `RecordPrintEndpoints.cs`: печать, предпросмотр, отладочный архив; ворота модуля-владельца и права
  формы; `WritesNothing` [CORE-21] [AUTH-8]. Проверка — `RecordPrintTests`: без права 403, модуль выключен
  — 501, закрытое поле отсутствует в контексте; `EndpointGateInventoryTests` и `ReadAllWritesNothingTests` зелёные
- [ ] 1.5 `core.print.edit` в `CorePermissions.cs`; ворота `/api/templates`, `/api/template-assets`,
  `/api/typst-userlib` по владельцу типа («любое из» для библиотеки) (решение В-13) [CORE-21].
  Проверка — тест: только `id.config.edit` — 403 на шаблон типа `plan`; тест состава `/api/account/access`
- [ ] 1.6 Редактор шаблонов показывает типы записей модулей (`features/templates/*`) без нового экрана.
  Проверка — vitest на список типов
- [ ] 1.7 `docs/DEV_NOTES.md`: как модулю объявить печатную форму; `docs/ADMIN_GUIDE.md`: право на шаблоны;
  `cd docs/tools && npm run toc`. Проверка — проверка содержания в «Frontend» зелёная

## 2. PR 2: форма КП

- [ ] 2.1 `Modules.Plan/Printing/OfferPrintContext.cs`: шапка, стройка, заказчик, строки с ценой продажи,
  итог, НДС по режиму КП (решение В-9), номер и дата версии; без себестоимости и наценки [PLAN-14]
  [PLAN-11]. Проверка — `OfferPrintTests`: у держателя `plan.estimate.cost` в контексте нет `cost` и `markup`
- [ ] 2.2 Объявить форму КП в `PlanModule.PrintForms` (право `plan.offer.read`); шаблон по умолчанию заводится
  в `InitializeAsync`, если у типа шаблона нет [PLAN-14]. Проверка — `PlanOnlyHostTests`: КП печатается без
  `id` (генератор подменён, проверяется контекст)
- [ ] 2.3 `docs/USER_GUIDE.md`: печать КП и где правится шаблон; `npm run toc`. Проверка — проверка
  содержания в «Frontend» зелёная

## 3. Верификация

- [ ] 3.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~RecordPrint|FullyQualifiedName~ModulePrintForm|FullyQualifiedName~OfferPrint|FullyQualifiedName~Generat|FullyQualifiedName~EndpointGateInventory|FullyQualifiedName~PlanOnlyHost"` — зелёный
- [ ] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 3.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
