# Proposal: свой просмотрщик PDF вместо встроенного в браузер, зонтик

## Why

Issue #1489 (эпик) и дочерние #1490, #1491, #1492, #1493, #1494, #1495.

PDF сейчас показывает встроенный просмотрщик браузера в `<iframe>`, в четырёх местах:
- вид для чтения счёта — `features/costs/InvoiceFileImage.tsx`;
- скан счёта — `features/costs/InvoiceScanPanel.tsx`;
- предпросмотр документа — `features/document-sets/editor/DocumentPreviewPanel.tsx`;
- поле-файл документа — `features/document-sets/fields/FileField.tsx`.

Чем это плохо:
- панель рисует браузер, со всем, что нам не нужно (рисование, подписи);
- в разных браузерах вид разный;
- на Android PDF во фрейме не показывается вовсе;
- предпросмотр документа после каждого «Обновить» прыгает на первую страницу;
- живые прогоны в CI PDF не видят: в headless-браузере просмотрщика нет, проверяется только наличие
  `<iframe>`.

Решения владельца от 18.10.2026 (`design.md`, D1–D6) задают состав работы: свой просмотрщик на pdf.js
со своей панелью, слой текста сразу, Ctrl+P печатает файл, на узком экране и телефоне — просмотрщик
поверх экрана, старым браузерам — отказ, строка-оговорка вида для чтения в одну строку.

Этот зонтик фиксирует общие решения, ловушки поставки, перечень дочерних change и их порядок.
Приоритет обычный: после эпика распознавания счёта #1479.

## What Changes

- Общий компонент `shared/ui/viewer` на ядре `pdfjs-dist` (ровно 6.4.299). У него своя панель (страницы,
  масштаб, поворот), слой текста, Ctrl+P, режим поверх экрана и названные отказы. Источник —
  байты, а не адрес. Первый потребитель — вид для чтения счёта (`viewer-01-component`).
- Скан счёта (PDF и фото) — тем же просмотрщиком. На экране уже 1280 px скан открывается поверх формы, а
  не отдельным окном (`viewer-02-invoice-scan`).
- Поле-файл документа — тем же просмотрщиком, в том числе на телефоне (`viewer-03-file-field`).
- Предпросмотр документа обновляется на том же листе, без пустого кадра. Заодно закрыта утечка
  `previewGeneratedFile` (`viewer-04-document-preview`).
- Фреймы закрыты: храповик `native-pdf-frame` на нуле, `frame-src` поставки сужен до фрейма печати,
  все открытия вкладки идут через одну функцию (`viewer-05-close-frames`).
- Строка-оговорка над видом для чтения счёта — в одну строку (`viewer-06-invoice-reading-note`).
- Сборка и поставка: worker через `?worker`, файлы рядом с библиотекой — своим плагином Vite
  без CDN, живые прогоны под CSP поставки, dependabot npm только для `pdfjs-dist`.

## Capabilities

### New Capabilities
- Нет в самом зонтике. Способность `sharedui` (общие компоненты интерфейса) получает требования
  просмотрщика `[UI-10]`…`[UI-27]` от дочерних change.

### Modified Capabilities
- Нет в самом зонтике (`skip_specs: true`). Дельты `sharedui`, `costs` и `id` несут дочерние change.

## Impact

- **Клиент:**
  - новый каталог `src/client/src/shared/ui/viewer/`;
  - места: `features/costs/{InvoiceFileImage,InvoiceScanPanel,LayoutChooser}.tsx`,
    `features/document-sets/editor/DocumentPreviewPanel.tsx`, `features/document-sets/fields/FileField.tsx`;
  - загрузчики `shared/api/{invoiceImage,invoices,attachments,documentSets}.ts` отдают `Blob`;
  - `shared/ui/ShortcutsHelp.tsx`, `vite.config.ts`, `eslint.config.js`, `package.json` (`pdfjs-dist`);
  - храповик интерфейса `scripts/ui-ratchet-lib.mjs` и `ui-baseline.json`;
  - живые прогоны `e2e/invoices-smoke.mjs`, `e2e/fields-smoke.mjs`.
- **Сервер:** только `viewer-06` — пометка о раскладке отдельным полем в ответе `/scan/image`.
- **Поставка:** `deploy/nginx.conf` (CSP), `.github/dependabot.yml` (npm для `pdfjs-dist`).
- **Документы:** `docs/USER_GUIDE.md` (оглавление — `npm run toc`), `docs/UI.md` (правило просмотрщика,
  исключение `iframe` у `native-title`), `docs/DEV_NOTES.md` (поставка pdf.js).
- **Версия** приложения не меняется ни одним PR: её поднимает CI. Функциональным PR — метка
  `version:minor`.
