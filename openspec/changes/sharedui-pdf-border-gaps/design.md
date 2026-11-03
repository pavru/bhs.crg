# Design

Контекст: граница браузеров и заплатки — D5 [`viewer-00-epic`](../archive/2026-10-26-viewer-00-epic/design.md);
сторож, который сам делает границу, — D6 [`infra-node26`](../infra-node26/design.md). Требование — [UI-21].

## D1. Что зовёт pdf.js 6.4.299 и чего нет на границе (сверено 04.11.2026)

Версии — по `@mdn/browser-compat-data` 8.1.8; строки — `node_modules/pdfjs-dist/build/`.

| Функция | Нет у | Где зовётся | Что видит пользователь |
|---|---|---|---|
| глобал `Iterator` | Firefox < 131, Safari < 18.4 | верх модуля `pdf.mjs:793`, `pdf.worker.mjs:784` (`typeof Iterator.prototype.join`) | `ReferenceError` при загрузке куска → «Просмотрщик не загрузился. Приложение обновилось — обновите страницу». Обновление не помогает |
| итераторные помощники | те же | `TextLayer` при выделении (`pdf.mjs:27608`), хранилище правок, worker: `hasJSActions`, XFA, сохранение | после починки глобала — `TypeError` в обработчике `selectionchange`, выделение текста без поправок pdf.js |
| `Set.prototype.intersection` | Firefox < 127 | worker, разбор именованных назначений при сборке страниц (`pdf.worker.mjs:62793`) | на путях просмотрщика — нет; путь библиотеки сломан |
| `Response.prototype.bytes` | Chrome < 132, Firefox < 128 | worker: `fetchBinaryData` — шрифты, cMap, wasm (`useWorkerFetch` включён: все три адреса заданы); главный поток — `fetchData` | отказ проглатывается: шрифты Symbol/ZapfDingbats подменены (галочки, маркеры), wasm-декодер JPX/JBIG2 → медленный запасной JS, встроенная cMap (CJK) не читается |
| `Blob.prototype.bytes` | Chrome < 144, Firefox < 128 | worker, картинки при сохранении (`pdf.worker.mjs:41940`) | на путях просмотрщика — нет |
| `ReadableStream` `for await` | Safari < 27 | `getTextContent` (`pdf.mjs:16016`), разжатие потоков в worker (`pdf.worker.mjs:3840`, в `try`) | `getTextContent` отказывает (просмотрщик его не зовёт — только тест); разжатие уходит в запасной JS |

Прочее новое в сборке на границе есть (`URL.parse` — ровно 126 / 126 / 18, `Promise.withResolvers`,
`AbortSignal.any`, `transferToFixedLength`) или проверяется самой библиотекой (`Float16Array`,
`buffer.resizable`).

## D2. Заплатки

Модуль прежний — `viewer/pdfPolyfills.ts`, подключён до библиотеки в `pdfEngine.ts` и `pdfWorker.ts`.
Правило прежнее: функцию, которая у среды есть, заплатка не трогает (`patch`).

- **`Iterator`** — абстрактный конструктор (вызов без `new` и `new Iterator()` — `TypeError`), его
  `prototype` — `%IteratorPrototype%` (достаётся как прототип прототипа итератора массива). Ставится на
  `globalThis` только при отсутствии.
- **Итераторные помощники** — все одиннадцать методов прототипа, на `%IteratorPrototype%`, а не на
  `Iterator.prototype`: глобала может не быть, а итераторы `Map`, `Set`, массивов и генераторов
  наследуют именно его. Ленивые (`map`, `filter`, `take`, `drop`, `flatMap`) — генераторы: их итог сам
  наследует `%IteratorPrototype%`, и цепочка `keys().filter(…).toArray()` работает. Аргументы
  проверяются как в стандарте там, где ошибка иначе ушла бы молча (не функция, отрицательный или
  нечисловой предел). Ставятся все, а не два зовущихся: набор — одно предложение стандарта, а сторож
  по имени (D4) потребовал бы почти все — `map`, `filter`, `some`, `find`… зовутся и у массивов.
- **`Set.prototype.intersection`** — только он: остальные методы `Set` сборка не зовёт (сторож D4
  скажет, когда начнёт). Аргумент — «подобное множеству» (`size`, `has`, `keys`), порядок итога —
  как в стандарте: обход меньшего.
- **`Response.prototype.bytes`, `Blob.prototype.bytes`** — `new Uint8Array(await this.arrayBuffer())`.
- **`ReadableStream.prototype.values` и `[Symbol.asyncIterator]`** — одна функция: асинхронный
  генератор над `getReader()`; при раннем выходе (`break`, `return`) поток отменяется, если не задан
  `preventCancel`; блокировка снимается всегда.

`IMPLS` дополняется новыми, тест «граница наступила» сверяет их места.

## D3. Снимок границы — Node 22 ∩ данные MDN

Генератор `scripts/browser-border.mjs`, как и прежде, снимает на `node:22.23.3-alpine` собственные
ключи встроенных объектов и выбрасывает из них **всё, чего по данным MDN нет хотя бы у одного браузера
границы** (`chrome: 126`, `firefox: 126`, `safari: 18`). Поддержано на версии B — есть запись без
`flags`, `prefix`, `alternative_name` и `partial_implementation`, где `version_added` — номер ≤ B
(`≤18` читается как 18), а `version_removed` нет или он > B. Ключа нет в данных — остаётся (это
служебное Node вроде `Buffer`): удалить его значило бы ломать среду наугад. Оставленный так ключ —
возможный пропуск, поэтому генератор печатает и выброшенное, и оставленное без данных, а пересъём
читается глазами.

Пути данных:
- встроенное ES — `javascript.builtins.<Объект>.<ключ>` (статические и прототипные — в одном ряду);
  `%TypedArray%` — `TypedArray`; символ — `@@<имя>`; глобал — запись конструктора
  `javascript.builtins.X.X`, иначе `javascript.builtins.X` или `api.X`;
- веб-API — `api.<Интерфейс>.<ключ>`, статический — сначала `<ключ>_static`.

**`%IteratorPrototype%` вместо `Iterator` / `Iterator.prototype`.** На границе глобала `Iterator` нет,
и помощник, удалив его первым, не нашёл бы `Iterator.prototype` и не убрал бы помощники. Путь
`%IteratorPrototype%` разбирает `browserBorderPaths.ts` (общий у генератора и теста) по самому
итератору. Статические `Iterator.from`/`concat` в снимок не входят: глобала нет — нет и их.

**Веб-API в снимке** — то, что зовёт библиотека: `Response`, `Blob`, `ReadableStream`, `WritableStream`,
`TransformStream`, `AbortSignal`, `URL`, `TextEncoder`, `TextDecoder`, `CompressionStream`,
`DecompressionStream` — конструктор и прототип. DOM (холст, `OffscreenCanvas`, `Path2D`, `FontFace`)
в Node не моделируется вовсе — его снимок не видит, как и прежде.

**Оставленное ради среды** — поле `kept` снимка, ключ → причина. На старте: `Error.captureStackTrace`
и `Error.stackTraceLimit` (у Firefox их нет, но без `stackTraceLimit` V8 не пишет стек — vitest
потерял бы место падения). Сторож D4 проверяет, что сборка их не зовёт.

Снимок записывает, по чему снят: `node`, `bcd` (версия данных), `browsers` (сама граница). Данные MDN — не зависимость
проекта: команда пересъёма ставит пакет во временный каталог контейнера и передаёт путь к `data.json`
ключом (`--bcd`). Без ключа генератор отказывает.

```bash
MSYS_NO_PATHCONV=1 docker run --rm -v "$PWD/src/client:/w" -w /w node:22.23.3-alpine sh -c \
  'npm i -s --prefix /tmp/bcd @mdn/browser-compat-data@8.1.8 && node scripts/browser-border.mjs --bcd /tmp/bcd/node_modules/@mdn/browser-compat-data/data.json'
```

## D4. Сторож по имени вызова

Загрузка эталонного PDF в Node проходит не все пути: worker в Node берёт файлы с диска, а не
`fetch`, выделения текста нет. Поэтому `pdfEngine.test.ts` получает второй, статический сторож:

- для каждого убранного границей **строкового** ключа объекта (не `globalThis`) ищется вызов по имени
  в `pdf.mjs` и `pdf.worker.mjs`: метод прототипа (и `%IteratorPrototype%`) — `.<имя>(`, статический —
  `<Объект>.<имя>(` (иначе `ReadableStream.from` совпадал бы с каждым `Array.from(`);
- найден — после загрузки `pdfEngine` (заплатки стоят) ключ обязан быть на месте; нет — красный со
  списком «метод — объект»;
- для ключей из `kept` вызова по имени быть не должно — иначе красный (оставленное ради среды не
  должно прикрывать вызов библиотеки).

Глобалы статическим сторожем не проверяются: библиотека проверяет их сама (`typeof Float16Array`), и
имя без вызова ничего не говорит. Глобал без проверки ловит загрузка модуля — как `Iterator`.
Символы — тоже динамически (`for await` — `doc.text()` в тесте).

Совпадение имён (`.filter(` зовётся и у массивов) даёт лишний красный, не пропуск — так и задумано:
заплатка дешевле разбора. Заведомо ложное совпадение записывается в тесте (`FALSE_MATCHES`) с
причиной, сверенной по тексту сборки; исключение, которому больше нечего прикрывать, краснит тест.
Найдено при реализации одно: `SharedArrayBuffer.prototype.grow` (нет у Firefox 126) — `.grow(` у
pdf.js зовётся у `WebAssembly.Memory` и `WebAssembly.Table`.

## D5. Чего не делаем

- **Граница не сдвигается** — это решение владельца (D5 `viewer-00-epic`). Поднять её до Firefox 131 /
  Safari 18.4 / Chrome 132 значило бы отказ «браузер устарел» там, где просмотрщик работает с
  заплатками в несколько КБ.
- **`viewerSupported()` не меняется**: `URL.parse` и `Promise.withResolvers` есть у всех трёх на
  границе, остальное закрывают заплатки.
- **Сборка `legacy/`** — нет, по прежней причине (вес).
- **Проверка в настоящих браузерах границы** в CI не заводится: сторож моделирует, ручная проверка —
  задача 3.3.
