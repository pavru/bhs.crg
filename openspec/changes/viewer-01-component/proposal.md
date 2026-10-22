# Proposal: просмотрщик PDF — компонент, панель, слой текста, Ctrl+P; вид для чтения счёта

Дочерний change зонтика `viewer-00-epic`, общая архитектура — `../viewer-00-epic/design.md` (D1–D5,
D7–D12).

## Why

Issue #1490 (эпик #1489, просмотрщик 1/5).

Без общего компонента не на что переводить ни одно из четырёх мест, где PDF показывает встроенный
просмотрщик браузера во фрейме. Компонент первым проверяет то, что в поставке ломается молча, а на dev
и в CI работает: модульный worker `.mjs` под mime-картой nginx, `blob:` под `connect-src 'self'`,
пустые сканы без `wasm/` (D8 зонтика).

Первым потребителем выбран вид для чтения счёта. Только в нём CI даёт настоящий PDF: конвертер
офисных файлов работает в живых прогонах. Значит, только здесь можно доказать, что лист нарисован, а
не что «есть `<iframe>`».

## What Changes

- Каталог `src/client/src/shared/ui/viewer/`: ленивый публичный компонент, движок — единственный
  импорт `pdfjs-dist`, чистое состояние показа, функция «Открыть во вкладке», печать файла.
- **API:** `source: Blob | Uint8Array`, `documentKey`, `title`, `actions`.
- **Панель** над листом: страница `‹ [2] / 5 ›`; масштаб `−` `[По ширине ▾]` `+`; поворот. В узкой
  панели (`< 420 px`) `−`/`+` уходят в меню.
- Слой текста, Ctrl+P печатает файл, режим поверх экрана, клавиши, ленивое рисование длинных
  документов, названные состояния и отказы (в том числе старый браузер), тёмная тема, доступность.
- **Сборка и поставка:**
  - `pdfjs-dist` ровно 6.4.299, worker через `?worker`;
  - файлы рядом с библиотекой — в `pdfjs/<версия>/` своим плагином Vite;
  - `vite preview` отдаёт CSP из `deploy/nginx.conf`;
  - dependabot npm только для `pdfjs-dist`;
  - `no-restricted-imports` на `pdfjs-dist`;
  - правило храповика `native-pdf-frame`.
- **Вид для чтения счёта** (`InvoiceFileImage.BuiltImage`):
  - показан просмотрщиком, `loadInvoiceImage` отдаёт `Blob`;
  - «Во весь экран» → «Открыть во вкладке»;
  - область листа — `relative isolate overflow-hidden`;
  - `invoices-smoke` переписан в этом же PR.
- `ShortcutsHelp` — строка про масштаб листа; `docs/USER_GUIDE.md` — панель, «Открыть во вкладке»,
  Ctrl+P; `docs/UI.md` — правило «PDF только через просмотрщик».

## Capabilities

### New Capabilities
- `sharedui`: общие компоненты интерфейса — `shared/ui`, оболочка, храповик интерфейса и правила
  docs/UI.md. Здесь — просмотрщик PDF `[UI-10]`…`[UI-23]`.

### Modified Capabilities
- `costs`: вид для чтения файла счёта показывается просмотрщиком (`[COST-6.2]`).

## Impact

- **Клиент:**
  - новые `shared/ui/viewer/*`;
  - правки `features/costs/InvoiceFileImage.tsx`, `shared/api/invoiceImage.ts`
    (`loadInvoiceImage` → `Blob`), `shared/ui/ShortcutsHelp.tsx`, `vite.config.ts`, `eslint.config.js`,
    `package.json`, `package-lock.json`, `scripts/ui-ratchet-lib.mjs`, `ui-baseline.json`;
  - `e2e/invoices-smoke.mjs`.
- **Поставка:** `.github/dependabot.yml`. `deploy/nginx.conf` не меняется: его только читает сторож
  CSP.
- **Документы:** `docs/USER_GUIDE.md` (13.4, оглавление — `npm run toc`), `docs/UI.md`,
  `docs/DEV_NOTES.md` (поставка pdf.js).
- **PR:** метки `version:minor` и `ui`, «Closes #1490».
