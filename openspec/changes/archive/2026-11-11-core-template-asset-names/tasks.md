# Tasks

## 1. Файлы полей — в `fields/` [CORE-21] [CORE-22]

- [x] 1.1 Константа `TypstGenerator.FieldsSubdir = "fields"`. `TypstCompileDirectory` пишет картинки полей (`TypstImageMaterializer`) и вложения (`fields/att_N.ext`) в неё, в `data.json` — `/fields/…` (D1). Параметр каталога у `TypstImageMaterializer.MaterializeAsync` назван `fieldsSubdir` и по умолчанию равен `fields`: новый вызов без аргумента не вернёт картинки полей в `assets/`. Проверка: `TypstCompileDirectoryTests.TemplateAsset_DoesNotOverwriteFieldFile` зелёный для `img_0` и `att_0` (случай `att_0` переведён на вложение-`.png` — у ассета `.pdf` не бывает), `Layout_IsTheDocumentedOne`, `TypstImageMaterializerTests` и прочие ожидания путей переписаны на `fields/`
- [x] 1.2 Доккомментарии раскладки (`TypstCompileDirectory`, `TypstImageMaterializer`, `TypstFileMaterializer`, `AssetPath`) и упоминания `assets/img_` / `assets/att_` в docs/ и `docs/DEV_NOTES.md` переписаны на `fields/`. Проверка: `git grep -n -E "assets/(img|att)_"` пуст вне тестов, которые проверяют сам `AssetPath`
- [x] 1.3 Совпадение комплекта с генерацией не потеряно. Проверка: `dotnet test … --filter "FullyQualifiedName~GenerationDebugBundleParityTests|FullyQualifiedName~TypstImageMaterializerTests|FullyQualifiedName~TypstFileMaterializerTests"` зелёный (нужен Typst CLI)

## 2. Правило имени ассета-картинки [CORE-21]

- [x] 2.1 `TemplateAsset.IsValidImageName(string)` в домене (D2): имя файла, а не путь — без `/`, `\`, `:`, `*`, `?`, `"`, `<`, `>`, `|`, `..`, управляющих знаков и пробела по краям; не пусто. Проверка: `TemplateAssetNameRuleTests` по общему набору `templateAssetNameCases.json` (`ГОСТ 21.101. Форма 3`, `Печать (скан)`, `Сечение 2,5мм²` — да; каждый запрещённый знак, `..`, пробел по краям, пусто — нет); тест держит, что каждый запрещённый знак в наборе есть
- [x] 2.2 `POST /api/template-assets` отказывает 400 `{ error }` с названными знаками ДО `blob.UploadAsync`, только для `Kind = Image`. `TemplateAssetHandlers` (Create) бросает `InvalidRequestException` на то же. Проверка: интеграционный тест — 400 на `схема/лист`, `logo:2` и `a..b`, хранилище не получило файла; `ГОСТ 21.101. Форма 3` и `Печать (скан)` создаются; шрифт `GOST 2.304 тип А` создаётся

## 3. Негодное имя из старых данных — отказ генерации [CORE-21] [CORE-22]

- [x] 3.1 `TypstCompileDirectory.InvalidAssetNames(request)` и `CompileLayout.InvalidAssetNames`: каталог не скачивает и не пишет ассет с негодным именем (D3). Проверка: тест на `TypstCompileDirectory` — дерево каталога с ассетом `схема|1` совпадает с деревом без него, имя в `InvalidAssetNames`, хранилище к его блобу не обращалось
- [x] 3.2 `InvalidTemplateAssetNamesException` (`BHS.CRG.Application/Generation`, 400). `TypstGenerator` бросает её ДО записи каталога. Проверка: тест генерации — 400, текст называет ассет и способ исправления, хранилище не прочитано ни разу
- [x] 3.3 `DebugBundleArchive.Readme` — строка `[invalid-asset-name] {Name}: …` в перечне причин отказа; README пишется и без других причин. Проверка: тест на `Readme` и тест комплекта — комплект отдаётся, файла ассета нет, README называет имя

## 4. Подсказка имени и пометка в панели (D4)

- [x] 4.1 `suggestAssetName(fileName)` и `isValidImageAssetName(name)` рядом с `src/client/src/shared/api/templateAssetRef.ts`; `TemplateAssetsPanel` подставляет подсказку в `window.prompt`. Проверка: vitest по общему набору `templateAssetNameCases.json` (то же, что у сервера) и на подсказку: `Печать (скан).png` → `Печать (скан)`, `схема:1.png` → `схема_1`, `a..b.svg` → `a.b`, `img.png` → `img`; из любого примера набора подсказка выходит годной или пустой
- [x] 4.2 `TemplateAssetsPanel` показывает ассет-картинку с негодным именем именем и пометкой «недопустимое имя, генерация откажет: удалите и загрузите заново», без ссылки `/assets/…` (D4). Проверка: `npx tsc -b`, `npm run ui:ratchet` зелёные; пометка тем же видом, что у шрифта с нераспознанным семейством

## 5. Верификация

- [x] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Generation|FullyQualifiedName~TemplateAsset"` зелёный
- [x] 5.2 `cd src/client && npx tsc -b`, `npm test -- templateAssetRef`, `npm run ui:ratchet`, `npm run lint:ratchet` зелёные
- [x] 5.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` зелёный
- [x] 5.4 `openspec validate core-template-asset-names --strict --no-interactive` проходит

## Workflow follow-up

- PR в master открывается после слияния PR #1901: ветка обновляется от master, коммиты #1901 из неё уходят.
- Описание PR называет цену для данных: ассет-картинка с недопустимым именем (двоеточие, кавычка и т. п.; пробел и скобки допустимы) в старых данных даёт отказ генерации, системная — всех документов; исправление — удалить и загрузить заново.
- Архив change — отдельным PR после слияния кода (`/opsx:archive`).
