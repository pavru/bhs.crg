# Design: поле-файл документа

Общая архитектура — `../viewer-00-epic/design.md` (D4, D7, D9). Компонент — `../viewer-01-component/design.md`.
Здесь только решения этого change.

## Context

- `FilePreviewModal` (`features/document-sets/fields/FileField.tsx:28`) — `Modal wide nothingToLose`,
  тело заводится заново ключом `blobPath` (issue #858).
- `FilePreviewBody` (`:41`) грузит `loadAttachmentObjectUrl` (`shared/api/attachments.ts:122`) и рисует:
  - картинку — `<img max-h-[70vh]>`;
  - PDF — `<iframe style={{ height: '70vh' }}>` (`:73`);
  - офисный файл — «Предпросмотр недоступен» и ссылку «Скачать».
- `Modal` ниже `sm` уже раскрывается на весь экран (`NARROW_FULL`, `shared/ui/Modal.tsx:83`).
- **Сверка: `loadAttachmentObjectUrl` зовут ещё двое** — `ImageField.tsx:253` и
  `openAttachmentInNewTab` (`attachments.ts:140`). Поэтому «вариант с `Blob`» — новая функция рядом, а не
  замена.
- `fields-smoke.mjs:286–289` ждёт `iframe` или `img` и читает `src` `blob:`.
- Вложение в посеве — `makePdf` (`e2e/seed.mjs:228`): одна страница, **невстроенная** Helvetica.
  Нарисованный в CI лист заодно доказывает, что `standard_fonts` поставлены: системных шрифтов у
  headless-браузера может не быть.

## Goals / Non-Goals

**Goals:** PDF поля-файла в просмотрщике; на телефоне — во весь экран и нарисован.

**Non-Goals:**
- Картинка поля-файла остаётся `<img>`: issue касается PDF, режим картинки приносит `viewer-02`, и
  порядок между ними свободный.
- Ссылка «Скачать» у офисного файла не трогается.
- `openAttachmentInNewTab` переводится в `viewer-05`.

## Decisions

- **Загрузчик.**
  - `loadAttachmentBlob(blobPath) → { blob, mimeType }` с тем же белым списком `VIEWABLE_TYPES`; чужой
    тип — `application/octet-stream`.
  - `loadAttachmentObjectUrl` строится на нём: поведение для `ImageField` и вкладки прежнее.
- **Показ.**
  - Категория `pdf` — `PdfViewer` с `documentKey = blobPath`, `title = fileName`.
  - Область — `h-[70vh] max-sm:h-full`, окно — `flushBody`.
  - Без явной высоты лента нулевой высоты, и лист пуст без ошибки (issue, «Чем ломается»).
  - Пока байтов нет, `source = null`: просмотрщик говорит «загружается».
- **Телефон.** Режим поверх экрана из `viewer-01` здесь даёт само окно: ниже `sm` `Modal` уже во весь
  экран, и второй слой поверх окна был бы лишним. Вид — по варианту, выбранному в `viewer-01` (0.1).
- **«Открыть во вкладке»** — кнопкой в слоте `actions` просмотрщика: у окна нет своей строки файла.
- **Храповик.** `FileField.tsx` уходит из уровня `native-pdf-frame`.

## Risks / Trade-offs

- [Тело окна прокручивается само, и лента получает высоту контента] → `flushBody` и явная высота;
  тест компонента проверяет класс высоты области.
- [Android в CI не проверяется] → ручная проверка на телефоне (Chrome Android) в описании PR; в CI —
  ширина 390 px в `fields-smoke`.
