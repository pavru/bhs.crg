# Design: просмотрщик PDF — компонент и вид для чтения счёта

Общая архитектура — `../viewer-00-epic/design.md` (D1–D5, D7–D12). Здесь только решения этого change.

## Context

- **Вид для чтения сейчас.** `InvoiceFileImage.BuiltImage` (`features/costs/InvoiceFileImage.tsx:59`)
  показывает `<iframe src={url}>` (`:133`). Адрес даёт `useImageUrl` (`:208`) через `loadInvoiceImage`
  (`shared/api/invoiceImage.ts:86`), тип — принудительно `application/pdf`. Ключ —
  `blobPath|builtAt`, а чужой ответ не показывается (`:231`).
- **Строка файла.** «Во весь экран» делает `window.open(url)` (`:85`).
- **Строка-оговорка** — `data-testid="image-caveat"`, в ней фраза, «Перестроить», `LayoutChooser`,
  пометки.
- **e2e.** `invoices-smoke.mjs:481` ждёт `iframe[title$="вид для чтения"]`. Эталон
  `converter-reference.xlsx` строится настоящим конвертером в живых прогонах.
- **Образцы.** Ленивый кусок — `CodeEditor.tsx` → `MonacoEditor.tsx`; worker — `?worker`; меню-чип —
  `LayoutChooser` (`Chip menu`); кнопки — `IconButton size="sm"` (32 px, правило UI.md); окно —
  `Modal fullScreen`; справка — `ShortcutsHelp` (`SHORTCUTS`).
- **Чего ещё нет.** Каталога `shared/ui/viewer` и пакета `pdfjs-dist` нет, `no-restricted-imports`
  нет, `vite preview` заголовков не отдаёт.

## Goals / Non-Goals

**Goals:**
- Компонент целиком: панель, слой текста, Ctrl+P, режим поверх экрана, состояния.
- Сборка, поставка и сторожа, которые ловят ловушки D8 зонтика.
- Один потребитель — вид для чтения счёта.

**Non-Goals:**
- Скан счёта и картинки — `viewer-02`. Поле-файл — `viewer-03`.
- Подмена документа без пустого кадра — `viewer-04`: здесь тот же ключ с новыми байтами просто
  перерисовывается с сохранённой позицией.
- Сужение `frame-src` — `viewer-05`. Строка-оговорка в одну строку — `viewer-06`.

## Decisions

### Состав каталога `shared/ui/viewer/`

| Файл | Роль |
|---|---|
| `PdfViewer.tsx` | публичный: только `lazy(() => import('./PdfViewerImpl'))` + `Suspense`, проверка возможностей браузера (D5 зонтика) до загрузки ядра; `preloadPdfViewer()` |
| `PdfViewerImpl.tsx` | лента страниц, режим поверх экрана, состояния |
| `ViewerToolbar.tsx` | панель: страница, масштаб (меню по образцу `LayoutChooser`), поворот; container query `@container` и порог 420 px |
| `pdfEngine.ts` | **единственный** импорт `pdfjs-dist`: worker `?worker`, `GlobalWorkerOptions.workerPort`, `wasmUrl`/`iccUrl`/`cMapUrl`/`standardFontDataUrl` → `pdfjs/<версия>/…`, `getDocument({ data })`, перевод исключений в наши виды отказа, `TextLayer`. Граница для `vi.mock` |
| `viewerState.ts` | чистое: шаги масштаба, «по ширине» и «страница целиком» с учётом поворота, окно видимых страниц бинарным поиском по накопленным высотам, якорь (страница + доля), потолок пикселей холста (`2**24` iOS, `2**25` иначе), нормализация номера |
| `openInNewTab.ts` | D9 зонтика: синхронное окно, свой тип, отложенный отзыв |
| `printPdf.ts` | Ctrl+P: скрытый фрейм через `document.createElement('iframe')`, `blob:` с `application/pdf`, `contentWindow.print()`, уборка |
| `textLayer.css` | ~40 строк по образцу пакета, на токенах; лист — `color-scheme: light`, выделение — синее с прозрачностью |

Каждый файл ниже 500 строк кода (храповик размера). Если лента не уложится, она делится на
«ленту» и «страницу».

### API

```ts
type PdfSource = Blob | Uint8Array;
interface PdfViewerProps {
  source: PdfSource | null;   // null — место ещё грузит байты; просмотрщик показывает «загружается»
  documentKey: string;        // новый — новый документ; тот же + новые байты — позиция сохраняется
  title: string;              // подпись region и имя для e2e
  actions?: ReactNode;        // слот кнопок места (не обязателен)
  rememberZoomAs?: string;    // ключ места для режима масштаба в localStorage (try/catch)
  overlay?: { open: boolean; onOpenChange(o: boolean): void };  // режим поверх экрана
}
```

`fullScreen` из предложения Архитектора не берём: «Открыть во вкладке» стоит у места (решение
Дизайнера), а просмотрщик экспортирует функцию.

### Движок

- Один worker на приложение: создаётся лениво и не гасится. Гасятся документы:
  `loadingTask.destroy()`, `doc.destroy()`.
- С `workerPort` запасного fake worker нет. Сбой запуска — наш отказ «просмотрщик не запустился», и
  чужой PDF не разбирается в главном потоке.
- `?worker` по умолчанию собирается классическим (`iife`), а pdf.js внутри делает `import()` запасных
  декодеров. Решение: `worker: { format: 'es' }` в `vite.config.ts` — и тогда прогон редактора шаблонов
  (Monaco) обязателен в том же PR. Не заработает — вернуть `iife` и проверить запасные декодеры на
  JPEG 2000 руками.
- Байты — `new Uint8Array(await blob.arrayBuffer())` на каждое открытие (передача буфера опустошает
  его у вызывающего).
- Аннотации рисуются в canvas (`AnnotationMode.ENABLE`); HTML-слой аннотаций, `enableXfa`,
  `enableScripting`, `pdf.sandbox*`, `quickjs*` не подключаются и не копируются.

### Лента и масштаб

- Контуры всех страниц — по `getViewport()` последовательно в фоне: размеры листов разные, А3 рядом
  с А4.
- Рисуются видимые ±2 (`IntersectionObserver`). Ушедший лист освобождает холст (`width = height = 0`,
  затем убрать — иначе Safari память не отдаёт), зовёт `page.cleanup()` и отменяет `RenderTask`.
- Смена масштаба: сразу CSS-`transform`, перерисовка через ≈150 мс. `ResizeObserver` пересчитывает
  «по ширине».
- HiDPI — с `devicePixelRatio` и потолком пикселей.

### Ctrl+P

Обработчик `keydown` на корне просмотрщика: с фокусом внутри — `preventDefault` и `printPdf(blob)`.
Встроенного PDF у браузера нет (`navigator.pdfViewerEnabled === false`) — вместо печати
`openInNewTab`. CSP поставки (`frame-src 'self' blob:`) фрейм печати пускает; его сужение в
`viewer-05` оставляет `blob:`.

### Режим поверх экрана

`Modal fullScreen headerless`: сверху своя строка с именем и «Закрыть», под ней тот же
`PdfViewerImpl`. Внутри — тот же документ, а не второй: байты те же, движок открывает свою копию.
Потребители — `viewer-02` (узкий экран счёта) и `viewer-03` (телефон). Здесь режим проверяется тестом
компонента.

### Сборка и поставка

- **Пакет.** `"pdfjs-dist": "6.4.299"` без `^`.
- **Плагин** `pdfjsAssets()` в `vite.config.ts`, около двадцати строк, без новой зависимости:
  - `generateBundle` → `emitFile` по каталогам `wasm/`, `iccs/`, `cmaps/`, `standard_fonts/` в
    `pdfjs/<версия>/`;
  - в dev `configureServer` отдаёт те же пути из `node_modules`;
  - список каталогов — экспортом, его читает сторож упаковки.
- **CSP в `vite preview`.** `preview.headers['Content-Security-Policy']` берётся разбором строки
  `add_header Content-Security-Policy` из `deploy/nginx.conf`. Dev-сервер CSP не получает: HMR
  вставляет инлайновые скрипты.
- **Сторож CSP** — vitest: строка `vite preview` совпадает с `nginx.conf`, разбор не пуст.
- **Сторож упаковки** — vitest, `node`:
  - читает `node_modules/pdfjs-dist/build/pdf.worker.mjs` и вынимает имена `.wasm` и `.icc`, которые
    просит worker;
  - сверяет их с содержимым каталогов списка.
- **Сторож `.mjs`** — шаг после `npm run build` в работе «Frontend» CI: в `dist` нет `*.mjs`.
  Ловушку `.mjs` не видят ни dev, ни `vite preview`.
- **dependabot.** Экосистема `npm`, `directory: /src/client`, `allow: [{ dependency-name: pdfjs-dist }]`,
  еженедельно. Пункт 5 шапки `dependabot.yml` переписывается: npm есть, но только для `pdfjs-dist`, и
  сказано почему. Этот же пункт правит открытый `core-mediator-migration`: он убирает оговорку про
  MediatR. Кто сливается вторым, тот сводит обе правки.
- **`no-restricted-imports`** в `eslint.config.js` на `pdfjs-dist` и `pdfjs-dist/*` вне
  `shared/ui/viewer/pdfEngine.ts`. Новое правило в храповике линта стартует с нуля.

### Вид для чтения счёта

- `loadInvoiceImage` возвращает `Blob` (тип `application/pdf`), отзывать нечего. `useImageUrl` →
  `useImageBytes` с тем же ключом и той же сверкой «чужой ответ не показываем».
- `documentKey` — `invoiceId|blobPath|builtAt`.
- «Во весь экран» (`Maximize2`) → «Открыть во вкладке» (`ExternalLink`) через `openInNewTab`.
- Область листа — `relative isolate overflow-hidden flex-1 min-h-0`.
- Отказ «не загрузился» (связь или хранилище) остаётся у места: это состояние ДО PDF.
- `rememberZoomAs="invoice-panel"`.

### Храповик `native-pdf-frame`

Правило в `RULES`:
- `count` по синтаксическому дереву — JSX-теги `iframe`, `embed`, `object` и вызовы
  `createElement('iframe'|'embed'|'object')`;
- `dirs` по умолчанию (`features/`);
- `instead` называет просмотрщик, `actions` и `openInNewTab`.

Базовый уровень:
- после этого PR `InvoiceFileImage.tsx` из уровня уходит;
- остаются `InvoiceScanPanel.tsx`, `DocumentPreviewPanel.tsx`, `FileField.tsx` по одному.

`ui-ratchet.test.mjs` — случаи: тег, `createElement`, имя `data-iframe` не считается, тип
`WeakMap<object, …>` не считается.

Почему по дереву, а не регуляркой `/<(?:iframe|embed|object)\b/` из разбора Архитектора: регулярка
считает и обобщённый тип `WeakMap<object, unknown[]>` (`shared/utils/rowIdentity.ts:46`). Как только
правило посмотрит в `shared/` (`viewer-05`), это даст ложное нарушение.

### Документы

- **`docs/USER_GUIDE.md` 13.4:**
  - панель просмотра;
  - «Открыть во вкладке» вместо «Во весь экран» — там поиск, печать с настройками, пароль;
  - Ctrl+P печатает файл.
  Оглавление — `npm run toc`.
- **`docs/UI.md`:** строка в таблице контролов «PDF и картинка — просмотрщик `shared/ui/viewer`» и
  абзац о храповике `native-pdf-frame`.
- **`docs/DEV_NOTES.md`, «Поставка»:**
  - почему `?worker` и почему не CDN;
  - каталог `pdfjs/<версия>/`;
  - dependabot только для `pdfjs-dist`;
  - `@napi-rs/canvas` в образе сборки.

### Макеты

Первая задача — три варианта в Claude Design и выбор владельца (D10 зонтика). Основа — разбор Дизайнера
от 18.10.2026. Варианты обязаны различаться решением, а не цветом: например, панель отдельной строкой,
панель слита со строкой файла через `actions`, нижняя панель. Разбор «плавающую пилюлю поверх листа не
делать» — ограничение для всех трёх.

## Risks / Trade-offs

- [`worker.format: 'es'` ломает Monaco] → прогон редактора шаблонов в том же PR; запасной путь — `iife`.
- [`vite preview` под CSP краснит живые прогоны по чужой причине] → чинится отдельным PR до этого и
  называется в описании.
- [Ctrl+P во фрейме `blob:` не печатает в Safari] → ручная проверка в трёх браузерах; если не печатает —
  ветка «нет встроенного PDF» и туда.
- [Невстроенные Arial/Times в счёте из 1С на телефоне без системных шрифтов] → `standard_fonts`
  (Liberation с кириллицей) в поставке; проверка на живом счёте при приёмке.
- [Битый ToUnicode портит копирование, но не рисование] → слой текста в подсказках не обещается.
- [`scripts/ui-ratchet-lib.mjs` — уже 469 строк, и вместе с комментариями новый счётчик может подвести
  его к порогу 500 строк кода] → счётчик выносится в свой модуль рядом, если `FileSizeRatchet` того
  потребует. Заранее не выносить.
