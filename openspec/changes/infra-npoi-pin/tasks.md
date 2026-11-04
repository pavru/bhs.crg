# Tasks

## 0. Вопросы владельцу

- [x] 0.1 Путь для #1756. Варианты: (а) принять EULA NPOI 2.8 и платить сбор; (б) самосборка 2.8.1 из исходников; (в) заменить NPOI на OpenXml SDK и убрать выгрузку в XLS; (г) остаться на ветке 2.7. **Ответ владельца 04.11.2026: (г) «Остаться на 2.7.x»** — design D1.

## 1. Версия NPOI и сторож лицензии

- [x] 1.1 `BHS.CRG.Infrastructure.csproj`: `NPOI` → диапазон `[2.7.4,2.7.5)` с комментарием (решение, причины EULA/LGPL, ссылка на #1756 и DEV_NOTES), по образцу MediatR в `BHS.CRG.Application.csproj` (D2). Проверка: в `obj/project.assets.json` разрешилась `NPOI/2.7.4`, и в зависимостях нет `NSax`; `cd src/server && dotnet build BHS.CRG.slnx` без NU1903 и NU1608.
- [x] 1.2 Если в 1.1 краснеет NU1903 или в 1.3 тест экспорта, образов печати или `ImageSharpCompatibilityTests` — откат на `[2.7.2,2.7.5)` (D2). Записать здесь, какая версия встала и почему. Проверка: запись в этой задаче и зелёный 1.3. **Встала 2.7.4** (04.11.2026): NU1903 и NU1608 нет, `NSax` в `project.assets.json` нет, 1.3 зелёный (244 теста). Отката не понадобилось. Единственная правка от подъёма — в тестах образов печати (`OldWorkbookRenditionTests`, `RenditionServiceTests`): в 2.7.4 перечисление видимости листа называется `SheetVisibility`, а не `SheetState` (три строки, продуктовый код его не использует).
- [x] 1.3 Проверить, что подъём ничего не сломал: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~SpreadsheetExporter|FullyQualifiedName~TableExport|FullyQualifiedName~DiscrepancyReport|FullyQualifiedName~Rendition|FullyQualifiedName~ImageSharpCompatibility|FullyQualifiedName~SystemDataSetDeclaration"` зелёный. Здесь же [CORE-33] «Выгрузка XLSX не зависит от шрифтов машины»: `SpreadsheetExporterFontTests` и ширины в `SpreadsheetExporterTests` зелёные на новой версии.
- [x] 1.4 `src/server/BHS.CRG.Tests/Configuration/NpoiLicenseTests.cs`: версия загруженной сборки NPOI (`typeof(NPOI.SS.UserModel.IWorkbook).Assembly`) < 2.7.5. Отказ называет ступень (2.7.5+ — NSax под LGPL-3.0, 2.8+ — EULA со сбором), решение #1756 и DEV_NOTES «Поставка» (D3). Проверка: тест зелёный; со временно подставленной границей ниже текущей версии — красный с этим текстом (проверить руками и вернуть).
- [x] 1.5 `src/server/Directory.Build.props`: NU1608 → `WarningsAsErrors`, с комментарием (D2; найдено ревью PR #1758). Без этого пакет из соседнего проекта поднимает NPOI до 2.7.5+ с одним предупреждением. Проверка: опыт на поддельном пакете — restore падает с NU1608; `dotnet build BHS.CRG.slnx` без ошибок.

## 2. Обход StreamingSheetFonts — постоянный [CORE-33]

- [x] 2.1 `StreamingSheetFonts.cs`: документация класса и текст отказа — по D4: переход закрыт решением, обход живёт, пока живёт NPOI 2.7; при отказе — сначала решение о NPOI, см. `NpoiLicenseTests`. Код не меняется. Проверка: `grep -n "2.8.1" src/server/BHS.CRG.Infrastructure/DataSets/StreamingSheetFonts.cs` не находит обещаний удалить файл; `SpreadsheetExporterTests` зелёный.
- [x] 2.2 `SpreadsheetExporterFontTests.cs`: сообщение `Changed(...)` — по D4 («NPOI сменили вопреки решению #1756»). Проверка: тест зелёный, в тексте нет «переход на NPOI 2.8».

## 3. Тексты вокруг решения

- [x] 3.1 `BHS.CRG.Infrastructure.csproj`: комментарии у `SixLabors.ImageSharp` и `System.Security.Cryptography.Xml` — вместо «убрать, когда NPOI перейдёт на…» записать, что NPOI на ветке 2.7 (#1756) тянет те же уязвимые версии и строка остаётся, а у ImageSharp ещё и ради PdfSharpCore. Проверка: в комментариях нет обещаний, которые ветка 2.7 не исполнит.
- [x] 3.2 `.github/dependabot.yml`, пункт 5: при включении экосистемы `nuget` NPOI исключить выше 2.7.4 (`ignore: dependency-name: NPOI, versions: [">=2.7.5"]`) — рядом с MediatR. Проверка: файл остаётся валидным YAML (правка только в комментарии).
- [x] 3.3 `docs/DEV_NOTES.md`, раздел «Поставка»: абзац «NPOI прибит к 2.7.4» рядом с абзацем о MediatR — EULA с 2.8.0, NSax LGPL в 2.7.5–2.7.6, рассмотренные пути (D1), сторож `NpoiLicenseTests`, когда возвращаться к вопросу (уязвимость в POIFS или HSSF — повод для change «замена NPOI»). Абзац «Выгрузка xlsx не меряет текст шрифтами машины» — по D4. Проверка: оба абзаца ссылаются на #1756, обещаний «обход убирается» нет.
- [x] 3.4 `TemplateAsset.cs`: в комментарии к `FontFamilyName` указано, что имя распознаёт SixLabors.Fonts, хотя читает его свой `FontFamilyNameReader`. Поправить попутно. Проверка: комментарий называет `FontFamilyNameReader`.

## 4. Верификация

- [x] 4.1 Весь набор backend двумя потоками: `bash scripts/backend-tests.sh` зелёный (в том числе `DomainExceptionPolicyTests` и `FileSizeRatchet`: `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet`).
- [x] 4.2 Живая проверка конвертера офисных файлов: `bash deploy/converter.tests.sh` зелёный (образы печати старых `.xls` читают контейнер через POIFS новой версии). **04.11.2026: 32 ок, 0 фейлов** с `PDFTOTEXT='docker run --rm -i minidocks/poppler pdftotext'`. Местный `pdftotext` из ucrt64 давал 7 ложных провалов (пустой текстовый слой). Сам скрипт NPOI не исполняет, он проверяет только контейнер. Чтение `.xls` через POIFS 2.7.4 покрыли тесты образов печати в 1.3.
- [x] 4.3 Клиент не затронут — `cd src/client && npx tsc -b` и `cd src/client && npm run ui:ratchet` для порядка зелёные.

## Workflow follow-up

- PR с `Closes #1756` и объяснением решения в описании; метка `dependencies`, без `version:minor`.
- После слияния — архив change (`/opsx:archive infra-npoi-pin`) отдельным PR; требование [CORE-33] о шрифтах переходит в `openspec/specs/core/spec.md`.
