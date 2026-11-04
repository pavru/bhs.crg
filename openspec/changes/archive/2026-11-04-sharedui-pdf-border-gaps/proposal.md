# Proposal

## Why

Граница просмотрщика PDF — Chrome и Firefox 126, Safari 18 (D5 `viewer-00-epic`). Заплатки
`pdfPolyfills.ts` закрывают только то, чего нет у **Chrome** 126: сторож ([UI-21]) убирал из среды
поверхность Node 22, а Node 22 по набору функций — это Chrome. Ревью PR #1747 (change `infra-node26`)
заподозрило, что для Firefox и Safari этого мало; сверка кода pdf.js 6.4.299 с данными совместимости
MDN (`@mdn/browser-compat-data` 8.1.8) подтвердила это — и нашла пробел хуже названного.

- **Firefox 126–130 и Safari 18.0–18.3: просмотрщик не открывается вовсе.** Сборка pdf.js на верхнем
  уровне модуля (и `pdf.mjs`, и `pdf.worker.mjs`) обращается к глобалу `Iterator`
  (`typeof Iterator.prototype.join`), а его там нет (Firefox 131, Safari 18.4). Загрузка куска
  просмотрщика падает с `ReferenceError`, и пользователь видит «Просмотрщик не загрузился. Приложение
  обновилось — обновите страницу» — неправду, которую обновление не лечит. Там же нет итераторных
  помощников (`keys().filter(…).toArray()` в слое текста — выделение текста).
- **Firefox 126: нет методов `Set`** (`intersection` — в worker, разбор именованных назначений при
  сборке страниц).
- **Chrome 126–131 (`Response`) / 126–143 (`Blob`), Firefox 126–127: нет `bytes()`.** Worker грузит им
  файлы рядом с библиотекой: шрифты Symbol и ZapfDingbats, встроенные cMap, wasm-декодеры. Отказ
  проглатывается — шрифт подменяется, декодер уходит в медленный запасной путь, cMap не читается.
- **Safari 18 (до 27): нет асинхронного обхода `ReadableStream`.** `getTextContent` (`for await`)
  отказывает; разжатие потоков в worker молча уходит в запасной путь.

Сторож этого не видел: снимок моделирует Chrome и только встроенное ES.

## What Changes

- **Заплатки** `pdfPolyfills.ts` (в обоих входах, только если функции нет): глобал `Iterator` и
  итераторные помощники на `%IteratorPrototype%` (`map`, `filter`, `take`, `drop`, `flatMap`, `reduce`,
  `toArray`, `forEach`, `some`, `every`, `find`); `Set.prototype.intersection`;
  `Response.prototype.bytes` и `Blob.prototype.bytes`; `ReadableStream.prototype.values` и
  `[Symbol.asyncIterator]`. Тесты заплаток — рядом.
- **Снимок границы — по самому слабому браузеру.** Генератор берёт поверхность Node 22, как прежде, и
  выбрасывает из неё всё, чего по данным MDN нет хотя бы у одного браузера границы. Снимок дополнен
  веб-API, которые зовёт библиотека (`Response`, `Blob`, `ReadableStream`, `URL`, `AbortSignal`…).
  Оставленное ради среды тестов (`Error.captureStackTrace`, `Error.stackTraceLimit`) названо в снимке
  с причиной.
- **Сторож по имени вызова.** Загрузка библиотеки в Node проходит не все пути (worker не качает файлы
  по сети): тест движка дополнительно сверяет, что каждый метод, убранный границей и вызываемый
  сборкой pdf.js по имени, после заплаток на месте.
- DEV_NOTES (абзац «Граница браузеров»), комментарии генератора, помощника, заплаток и ворот.

Граница не сдвигается, `viewerSupported()` не меняется: отказ «браузер устарел» — по-прежнему ниже
126 / 18.

## Capabilities

### New Capabilities
<!-- нет -->

### Modified Capabilities
- `sharedui`: [UI-21] — сценарий «Браузер на границе» называет все три браузера и запуск куска
  просмотрщика; сценарий «Обновлённая библиотека вызывает новую функцию» — граница по самому слабому
  браузеру, включая веб-API; новый сценарий — функция на пути, который тест не проходит.

## Impact

- `src/client/src/shared/ui/viewer/`: `pdfPolyfills.ts` (+ тест), `pdfEngine.test.ts`,
  комментарий `viewerGate.ts`.
- `src/client/src/shared/testing/`: `browserBorder.json` (пересъём), `browserBorder.ts`,
  `browserBorderPaths.ts`; генератор `src/client/scripts/browser-border.mjs`.
- `docs/DEV_NOTES.md`. Зависимостей проекта не прибавляется: данные MDN генератор берёт ключом
  командной строки, ставит их в контейнер команда пересъёма.
- Сервер не затрагивается; `<Version>` не меняется.
