# Tasks: поле-файл документа

Условие — `viewer-01-component` слит. Своих макетов нет: вид — по варианту, выбранному в `viewer-01` (0.1).

## 1. Загрузчик

- [x] 1.1 `loadAttachmentBlob(blobPath) → Blob` с белым списком `VIEWABLE_TYPES`;
  `loadAttachmentObjectUrl` построен на нём. Проверка — `npx tsc -b`; `ImageField` и
  `openAttachmentInNewTab` работают как прежде (проверка картинки в `fields-smoke` зелёная)

## 2. Окно поля-файла

- [x] 2.1 `FilePreviewBody`, категория `pdf`: `PdfViewer` (`documentKey = blobPath`), область
  `h-[70vh] max-sm:flex-1`, окно `headerless flushBody`, «Открыть во вкладке» в верхней строке окна
  (`ViewerTitleBar`, вынесен из `PdfViewerOverlay`), «Скачать» в `actions`. Проверка — тест
  компонента с подменённым движком: область просмотра есть, её высота задана; офисный файл —
  прежний текст и «Скачать»; `FileField.tsx` ушёл из уровня `native-pdf-frame`
- [x] 2.2 `fields-smoke.mjs:286–289` переписан:
  - для PDF — область `role=region` в окне и canvas первого листа не пуст;
  - проверка поля-картинки (`:309`, `naturalWidth`) не меняется;
  - на ширине 390 px окно во весь экран и лист нарисован.
  Проверка — живой прогон зелёный в CI
- [x] 2.3 Ручная проверка на телефоне (Chrome Android): PDF поля-файла виден, кнопки браузера «Открыть»
  на месте листа нет. Проверка — описание PR

## 3. Верификация

- [x] 3.1 `cd src/client && npm test` — зелёный
- [x] 3.2 `cd src/client && npx tsc -b` — без ошибок
- [x] 3.3 `cd src/client && npm run ui:ratchet` — без роста, уровень `native-pdf-frame` опущен
- [x] 3.4 `cd src/server && dotnet test BHS.CRG.Tests/BHS.CRG.Tests.csproj --filter FileSizeRatchet` — зелёный

## Workflow follow-up

- PR: метки `version:minor` и `ui`; «Closes #1492».
