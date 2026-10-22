# Tasks: шаблон документа выбирается одним правилом

Функциональный PR (bug) — без метки `version:minor`, в описании `Closes #1401`.

## 1. Одна функция выбора

- [ ] 1.1 `Application/Generation/TemplateChoice.cs`: набор `TemplateIds` в порядке набора (активные;
  ни одного — отказ) → явный `TemplateId` шаблона этого типа → по умолчанию (активный) → самый ранний
  активный по `CreatedAt`, `Id` (D1–D5) [ID-3]. Проверка — юнит-тесты функции: каждое звено цепочки,
  порядок кандидатов не влияет на ответ, явный шаблон чужого типа не берётся
- [ ] 1.2 `GenerateDocumentHandler`, `PreviewDocument`, `GetGenerationDebugBundle` берут шаблон
  только через `TemplateChoice`; неверные комментарии «тот же выбор» / «зеркалит» заменить ссылкой на
  функцию [ID-3]. Проверка — grep `IsDefault && t.IsActive` в `Application/Generation` пуст
- [ ] 1.3 `ListTemplatesQuery` отдаёт шаблоны в порядке `CreatedAt`, `Id` (D6) [ID-3]. Проверка —
  интеграционный тест порядка списка
- [ ] 1.4 Сторож [ID-3]:
  - два активных шаблона, экземпляр с `TemplateIds` — генерация, предпросмотр и отладочный пакет
    берут один и тот же (по `template.typ` пакета и шаблону выпущенного файла);
  - два активных без «по умолчанию», вставленные в обратном порядке `CreatedAt`, — все трое берут
    ранний;
  - все выбранные неактивны — у всех троих один отказ.
  Проверка — на master тест красный (пакет берёт не тот шаблон), с 1.2 — зелёный
- [ ] 1.5 До слияния — запрос на копии базы заказчика: сколько типов имеют больше одного активного
  шаблона без «по умолчанию»; число — в описании PR. Проверка — отчёт в PR

## 2. Верификация

- [ ] 2.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~TemplateChoice|FullyQualifiedName~Generation|FullyQualifiedName~Preview|FullyQualifiedName~TemplateInvalidation"` — зелёный
- [ ] 2.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 2.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 2.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный
