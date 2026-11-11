# Tasks: вход Typst для генерации и отладочного пакета — одним кодом

Функциональный PR без метки `version:minor` (PATCH): маршруты прежние, меняется содержимое пакета. Issue
нет — источник: репетиция выпуска 2.105.0, 11.11.2026.

## 1. Сторож — сначала красный

- [x] 1.1 `Integration/GenerationDebugBundleParityTests` на настоящем Typst: сид (тип с картинкой и
  вложением-PDF, шаблон с ассетом-картинкой и шрифтом, экземпляр). Сценарии [CORE-21] [CORE-22]: выпуск +
  пакет того же экземпляра — набор файлов и разобранный `data.json` совпадают, PDF из распакованного
  пакета совпадает с выпущенным по тексту (PdfPig) и числу листов. Каталог генерации снимается в тесте,
  без флага в рабочем коде (D6). Проверка — на master сценарий зелёный (базовая линия: сегодня они
  совпадают на простом сиде)
- [x] 1.2 Сценарии расхождений (D6) [CORE-21] [CORE-22]: ассет больше предела, шрифт объявлен и не
  скачался, отмена на загрузке ассета, `missing-required`, нечитаемая картинка. Проверка — на master
  красные все пять, и каждый называет своё расхождение
- [x] 1.3 Регрессионные тесты поведения до перехода: предпросмотр незаполненного документа рисуется;
  проверка документа (`GET /api/generate/validate/{id}`) возвращает на сиде тот же набор кодов и путей;
  генерация с `missing-required` — 422. Проверка — зелёные на master

## 2. Сборка контекста — одна функция

- [x] 2.1 `Application/Generation/GenerationContextBuilder.cs`: семь шагов, корзины `Resolution` и
  `Checks`, вход со снимком схем (D1) [CORE-21]. Проверка — юнит-тест порядка шагов на подставных
  резолверах и распределения диагностики по корзинам
- [x] 2.2 На него переходят `GenerateDocumentHandler`, `PreviewDocument`, `InstanceResolutionValidator`
  (пакетная сверка `QualitySetAudit` по-прежнему передаёт свой снимок схем) и `GetGenerationDebugBundle`
  [CORE-21]. Комментарии «зеркалит / менять синхронно» убрать. Проверка — `grep ResolveComputedFieldsAsync`
  в `Application` вне строителя пуст; тесты 1.3 зелёные
- [x] 2.3 `Application/Generation/GenerationRequestBuilder.cs`: штамп `_type`, `params`, блоки типов,
  библиотека, ассеты на шаблон (D2) [CORE-21]; генерация, предпросмотр и пакет строят запрос только им.
  Проверка — `grep "new GenerationRequest("` в `Application/Generation` — только строитель

## 3. Каталог компиляции — одна запись

- [x] 3.1 `Infrastructure/Generation/TypstCompileDirectory.cs`: запись каталога с пределом 100 МБ для
  ассетов и вложений, проброшенной отменой, `FontsDir` только при записанном шрифте, нечитаемые картинки
  в `CompileLayout` (D3) [CORE-21] [CORE-22]. Проверка — юнит-тесты на подставном хранилище: предел, отмена,
  шрифт не скачался, раскладка путей
- [x] 3.2 `TypstGenerator.GenerateAsync` — `WriteAsync` + компиляция; новый `IDocumentGenerator
  .DebugBundleAsync` — `WriteAsync(Indented)` + README (D5) + zip каталога (D3) [CORE-21] [CORE-22].
  Проверка — тесты 1.1 зелёные
- [x] 3.3 `GetGenerationDebugBundleHandler` возвращает байты пакета, эндпоинт в `GenerationEndpoints.cs`
  только отдаёт файл (D4); 404 на неизвестный экземпляр сохраняется. Проверка — `grep
  TypstFileMaterializer src/server/BHS.CRG.Api` пуст; тесты 1.2 зелёные

## 4. Документация

- [x] 4.1 Комментарий-обещание у эндпоинта и у `GenerationDebugBundle` («ровно те файлы, что генератор
  кладёт…») переписать ссылкой на `TypstCompileDirectory`; в `docs/ADMIN_GUIDE.md` — раздел про
  отладочный пакет: README называет причины отказа генерации, `--font-path` только по README [CORE-21].
  Проверка — `cd docs/tools && npm run toc` не меняет оглавление, или изменение закоммичено

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~GenerationDebugBundle|FullyQualifiedName~GenerationContextBuilder|FullyQualifiedName~TypstCompileDirectory|FullyQualifiedName~Generation|FullyQualifiedName~QualitySetAudit"` — зелёный
- [ ] 5.2 `cd src/client && npx tsc -b` — без ошибок
- [ ] 5.3 `cd src/client && npm run ui:ratchet` — без роста
- [ ] 5.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный; если `GenerationEndpoints.cs` или `TypstGenerator.cs` похудели ниже уровня в `src/file-size-baseline.json` — уровень опущен тем же PR
- [ ] 5.5 `openspec validate core-generation-input-one-path --strict --no-interactive` — без ошибок

## Workflow follow-up

- После слияния кода — архивный PR (`/opsx:archive core-generation-input-one-path`).
- `plan-15-record-print` при старте берёт `GenerationRequestBuilder` и `DebugBundleAsync` для печати
  записей и своего отладочного архива.
