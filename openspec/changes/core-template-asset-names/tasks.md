# Tasks

## 1. Файлы полей — в `fields/` [CORE-21] [CORE-22]

- [ ] 1.1 Константа `TypstGenerator.FieldsSubdir = "fields"`. `TypstCompileDirectory` пишет картинки полей (`TypstImageMaterializer`) и вложения (`fields/att_N.ext`) в неё, в `data.json` — `/fields/…` (D1). Проверка: `TypstCompileDirectoryTests.TemplateAsset_DoesNotOverwriteFieldFile` зелёный для `img_0` и `att_0` (случай `att_0` переведён на вложение-`.png` — у ассета `.pdf` не бывает), `Layout_IsTheDocumentedOne` и прочие ожидания путей переписаны на `fields/`
- [ ] 1.2 Доккомментарии раскладки (`TypstCompileDirectory`, `TypstImageMaterializer`, `TypstFileMaterializer`, `AssetPath`) и упоминания `assets/img_` / `assets/att_` в docs/ и `docs/DEV_NOTES.md` переписаны на `fields/`. Проверка: `git grep -n -E "assets/(img|att)_"` пуст вне тестов, которые проверяют сам `AssetPath`
- [ ] 1.3 Совпадение комплекта с генерацией не потеряно. Проверка: `dotnet test … --filter "FullyQualifiedName~GenerationDebugBundleParityTests|FullyQualifiedName~TypstImageMaterializerTests|FullyQualifiedName~TypstFileMaterializerTests"` зелёный (нужен Typst CLI)

## 2. Правило имени ассета-картинки [CORE-21]

- [ ] 2.1 `TemplateAsset.IsValidImageName(string)` в домене (D2): буквы, цифры, `-`, `_`, `.`; без `/`, `\`, `..`; не пусто. Проверка: юнит-тест на таблице случаев (`img_0`, `логотип-2.v1` — да; `fields/img_0`, `a\b`, `..`, `a..b`, `Логотип компании`, пусто — нет)
- [ ] 2.2 `POST /api/template-assets` отказывает 400 `{ error }` с названными знаками ДО `blob.UploadAsync`, только для `Kind = Image`. `TemplateAssetHandlers` (Create) бросает `InvalidRequestException` на то же. Проверка: интеграционный тест — 400 на `fields/img_0` и на имя с пробелом, хранилище не получило файла; шрифт `GOST 2.304 тип А` создаётся; допустимое имя создаётся

## 3. Негодное имя из старых данных — отказ генерации [CORE-21] [CORE-22]

- [ ] 3.1 `CompileLayout.InvalidAssetNames`: `TypstCompileDirectory` не скачивает и не пишет ассет с негодным именем, а вносит имя в список (D3). Проверка: тест на `TypstCompileDirectory` — ассет `../img_0` не оставляет файла ни в `_dir`, ни вне его, имя в `InvalidAssetNames`, хранилище к его блобу не обращалось
- [ ] 3.2 `InvalidTemplateAssetNamesException` (`BHS.CRG.Application/Generation`, 400). `TypstGenerator` бросает её до компиляции. Проверка: тест генерации — 400, текст называет ассет и способ исправления
- [ ] 3.3 `DebugBundleArchive.Readme` — строка `[invalid-asset-name] {Name}: …` в перечне причин отказа; README пишется и без других причин. Проверка: тест на `Readme` и тест комплекта — комплект отдаётся, файла ассета нет, README называет имя

## 4. Подсказка имени в окне загрузки (D4)

- [ ] 4.1 `suggestAssetName(fileName)` рядом с `src/client/src/shared/api/templateAssetRef.ts`; `TemplateAssetsPanel` подставляет её в подсказку `window.prompt`. Проверка: vitest на `Логотип компании (2).png` → `Логотип_компании_2_` (или равный по правилу), `a..b.svg` → `a.b`, `img.png` → `img`

## 5. Верификация

- [ ] 5.1 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter "FullyQualifiedName~Generation|FullyQualifiedName~TemplateAsset"` зелёный
- [ ] 5.2 `cd src/client && npx tsc -b`, `npm test -- templateAssetRef`, `npm run ui:ratchet`, `npm run lint:ratchet` зелёные
- [ ] 5.3 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` зелёный
- [ ] 5.4 `openspec validate core-template-asset-names --strict --no-interactive` проходит

## Workflow follow-up

- PR в master открывается после слияния PR #1901: ветка обновляется от master, коммиты #1901 из неё уходят.
- Описание PR называет цену для данных: ассет-картинка с недопустимым именем (пробел, скобки) в старых данных даёт отказ генерации, исправление — удалить и загрузить заново.
- Архив change — отдельным PR после слияния кода (`/opsx:archive`).
