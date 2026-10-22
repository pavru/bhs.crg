# Proposal: просмотрщик PDF — поле-файл документа (и телефон)

Дочерний change зонтика `viewer-00-epic`, общая архитектура — `../viewer-00-epic/design.md` (D4, D7, D9).

## Why

Issue #1492 (эпик #1489, просмотрщик 3/5).

Окно предпросмотра поля-файла (`FileField.FilePreviewBody`) показывает PDF встроенным просмотрщиком
браузера в `<iframe>`. На телефоне это единственное место приложения, где PDF вообще встречается, и
там оно не работает:
- **Chrome на Android** на месте листа показывает кнопку «Открыть»;
- **Safari на iOS** показывает во фрейме только первую страницу.

Наш просмотрщик рисует лист сам и одинаково везде.

## What Changes

- `FilePreviewBody`, категория `pdf`: `<iframe>` → просмотрщик из `viewer-01`. Байты даёт новый
  `loadAttachmentBlob`, белый список типов прежний.
- Высота области листа задана явно: `70vh` на компьютере, весь экран окна на телефоне.
- На телефоне окно поля-файла уже раскрывается на весь экран (`Modal`, `NARROW_FULL`). Это и есть
  режим поверх экрана, второго слоя нет.
- «Открыть во вкладке» в окне — общая функция из `viewer-01`.
- `fields-smoke` переписан в этом PR. `FileField.tsx` уходит из уровня храповика `native-pdf-frame`.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `id`: PDF в окне поля-файла документа показывается общим просмотрщиком, в том числе на телефоне.

## Impact

- **Клиент:**
  - `features/document-sets/fields/FileField.tsx`;
  - `shared/api/attachments.ts`: новый `loadAttachmentBlob`, `loadAttachmentObjectUrl` построен на нём;
  - `ui-baseline.json`;
  - `e2e/fields-smoke.mjs`.
- **Документы:** нет. `docs/USER_GUIDE.md` окна поля-файла не описывает (сверено grep); панель
  просмотра описывает `viewer-01`.
- **PR:** метки `version:minor` и `ui`, «Closes #1492». Зависит от `viewer-01-component`.
