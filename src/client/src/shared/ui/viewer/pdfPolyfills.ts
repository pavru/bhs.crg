/**
 * Заплатки функций, которые вызывает сборка pdf.js 6 и которых нет у браузеров на границе поддержки
 * просмотрщика — Chrome 126, Firefox 126, Safari 18 (D5 зонтика viewer, поправка от 25.10.2026;
 * пробелы Firefox и Safari — change sharedui-pdf-border-gaps).
 *
 * Без них просмотрщик падал бы при запуске, а не показывал отказ: `Map.getOrInsertComputed` есть с
 * Chrome 145 и Safari 26.2, `Math.sumPrecise` — с Chrome 147, а к глобалу `Iterator` сборка
 * обращается на верхнем уровне модуля — без него (Firefox до 131, Safari до 18.4) кусок просмотрщика
 * не загружается вовсе. `Response.bytes` (Chrome до 132) worker'у нужен для шрифтов, cMap и wasm.
 * Подключается ДО pdf.js дважды — в главном потоке (`pdfEngine.ts`) и во входе worker'а
 * (`pdfWorker.ts`): глобалов главного потока worker не видит.
 *
 * Функцию, которая у браузера есть, заплатка не трогает. Сторож полноты — тест настоящего движка
 * (`pdfEngine.test.ts`): до загрузки библиотеки он убирает из среды всё, чего нет на границе по
 * данным совместимости MDN (`browserBorder.ts`), и сверяет, что каждый убранный метод, который
 * сборка зовёт по имени, после заплаток на месте. Обновлённый pdf.js, вызвавший новую функцию без
 * заплатки, покраснит его.
 */

/** Ставит `impl` под именем `name`, если его нет. `true` — поставлена. */
export function patch(target: object, name: PropertyKey, impl: unknown): boolean {
  if (name in target) return false;
  Object.defineProperty(target, name, { value: impl, writable: true, configurable: true, enumerable: false });
  return true;
}

type Keyed<K, V> = { has(k: K): boolean; get(k: K): V | undefined; set(k: K, v: V): unknown };

function getOrInsert<K, V>(this: Keyed<K, V>, key: K, value: V): V {
  if (!this.has(key)) this.set(key, value);
  return this.get(key) as V;
}

function getOrInsertComputed<K, V>(this: Keyed<K, V>, key: K, compute: (key: K) => V): V {
  if (typeof compute !== 'function') throw new TypeError('getOrInsertComputed: нужна функция');
  if (!this.has(key)) this.set(key, compute(key));
  return this.get(key) as V;
}

/**
 * Сумма без накопления ошибки округления: частичные суммы Шевчука и итоговое округление, как
 * `math.fsum` в Python. Переполнение промежуточной суммы конечных чисел не разбирается — pdf.js
 * складывает длины и размеры.
 */
function sumPrecise(items: Iterable<number>): number {
  const partials: number[] = [];
  let nan = false, plusInf = false, minusInf = false, onlyMinusZero = true;
  for (const item of items) {
    if (typeof item !== 'number') throw new TypeError('Math.sumPrecise: нужны числа');
    if (Number.isNaN(item)) { nan = true; continue; }
    if (item === Infinity) { plusInf = true; continue; }
    if (item === -Infinity) { minusInf = true; continue; }
    if (!Object.is(item, -0)) onlyMinusZero = false;
    let x = item, i = 0;
    for (let y of partials) {
      if (Math.abs(x) < Math.abs(y)) [x, y] = [y, x];
      const hi = x + y;
      const lo = y - (hi - x);
      if (lo) partials[i++] = lo;
      x = hi;
    }
    partials.length = i;
    partials.push(x);
  }
  if (nan || (plusInf && minusInf)) return NaN;
  if (plusInf) return Infinity;
  if (minusInf) return -Infinity;
  if (onlyMinusZero) return -0;
  let n = partials.length;
  let hi = partials[--n] ?? 0, lo = 0;
  while (n > 0) {
    const x = hi, y = partials[--n];
    hi = x + y;
    lo = y - (hi - x);
    if (lo) break;
  }
  if (n > 0 && ((lo < 0 && partials[n - 1] < 0) || (lo > 0 && partials[n - 1] > 0))) {
    const y = lo * 2, x = hi + y;
    if (y === x - hi) hi = x;
  }
  return hi;
}

function promiseTry<T>(fn: (...args: unknown[]) => T | PromiseLike<T>, ...args: unknown[]): Promise<T> {
  return new Promise<T>(resolve => resolve(fn(...args)));
}

interface Base64Options { alphabet?: 'base64' | 'base64url'; omitPadding?: boolean }

function toBase64(this: Uint8Array, options: Base64Options = {}): string {
  let binary = '';
  for (let i = 0; i < this.length; i += 0x8000) binary += String.fromCharCode(...this.subarray(i, i + 0x8000));
  let out = btoa(binary);
  if (options.alphabet === 'base64url') out = out.replace(/\+/g, '-').replace(/\//g, '_');
  return options.omitPadding ? out.replace(/=+$/, '') : out;
}

function fromBase64(text: string, options: Base64Options = {}): Uint8Array {
  if (typeof text !== 'string') throw new TypeError('Uint8Array.fromBase64: нужна строка');
  let clean = text.replace(/[\t\n\f\r ]/g, '');
  if (options.alphabet === 'base64url') clean = clean.replace(/-/g, '+').replace(/_/g, '/');
  let binary: string;
  try { binary = atob(clean); } catch { throw new SyntaxError('Uint8Array.fromBase64: строка не base64'); }
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function toHex(this: Uint8Array): string {
  let out = '';
  for (const byte of this) out += byte.toString(16).padStart(2, '0');
  return out;
}

/** `%IteratorPrototype%` — общий прототип итераторов: глобала `Iterator` на границе может не быть. */
const ITERATOR_PROTO = Object.getPrototypeOf(Object.getPrototypeOf([][Symbol.iterator]())) as object;

/** Глобал `Iterator` — абстрактный: ни вызвать, ни создать напрямую. */
function iteratorCtor(): void {
  if (new.target === undefined || new.target === iteratorCtor) throw new TypeError('Iterator: абстрактный конструктор');
}
Object.defineProperty(iteratorCtor, 'name', { value: 'Iterator' });
Object.defineProperty(iteratorCtor, 'prototype', { value: ITERATOR_PROTO, writable: false });

interface Direct { it: object; next(): IteratorResult<unknown> }

/** Итератор и его `next`, прочитанный один раз, — как в стандарте. */
function direct(it: unknown, name: string): Direct {
  if ((typeof it !== 'object' && typeof it !== 'function') || it === null) throw new TypeError(`${name}: нужен итератор`);
  const next = (it as { next: () => IteratorResult<unknown> }).next;
  return { it, next: () => next.call(it) };
}

/** Закрыть итератор (`return`), если он это умеет. */
function close(it: object): void {
  const ret = (it as { return?: () => unknown }).return;
  if (typeof ret === 'function') ret.call(it);
}

/** Отказ с закрытием источника: наружу уходит сам отказ, а не отказ закрытия. */
function fail(src: Direct | undefined, error: unknown): never {
  try { if (src) close(src.it); } catch { /* стандарт отдаёт исходный отказ */ }
  throw error;
}

/** Неверный аргумент помощника закрывает источник — так в действующей редакции стандарта. */
function needFn(f: unknown, name: string, src?: Direct): asserts f is (...args: unknown[]) => unknown {
  if (typeof f !== 'function') fail(src, new TypeError(`${name}: нужна функция`));
}

function limitOf(limit: unknown, name: string, src: Direct): number {
  let n: number;
  try { n = Number(limit); } catch (e) { fail(src, e); }
  if (Number.isNaN(n)) fail(src, new RangeError(`${name}: предел не число`));
  if (Math.trunc(n) < 0) fail(src, new RangeError(`${name}: предел меньше нуля`));
  return Math.trunc(n);
}

/**
 * Ленивый помощник — генератор над источником; `step` отдаёт значения и возвращает `true`, чтобы
 * закончить. Источник закрывается, когда потребитель ушёл раньше или обработчик отказал, но не
 * тогда, когда отказал или кончился сам источник.
 */
function* lazy(src: Direct, step: (value: unknown, index: number) => Generator<unknown, boolean | void>): Generator<unknown, undefined> {
  let open = false;
  try {
    for (let index = 0; ; index++) {
      const r = src.next();
      if (r.done) return undefined;
      open = true;
      const stop = yield* step(r.value, index);
      open = false;
      if (stop) { close(src.it); return undefined; }
    }
  } finally {
    if (open) close(src.it);
  }
}

function iterMap(this: unknown, f: unknown) {
  const src = direct(this, 'map'); needFn(f, 'map', src);
  return lazy(src, function* (v, i) { yield f(v, i); });
}

function iterFilter(this: unknown, f: unknown) {
  const src = direct(this, 'filter'); needFn(f, 'filter', src);
  return lazy(src, function* (v, i) { if (f(v, i)) yield v; });
}

function iterTake(this: unknown, limit: unknown) {
  const src = direct(this, 'take');
  let left = limitOf(limit, 'take', src);
  return (function* () {
    if (left === 0) { close(src.it); return undefined; }
    yield* lazy(src, function* (v) { yield v; return --left === 0; });
  })();
}

function iterDrop(this: unknown, limit: unknown) {
  const src = direct(this, 'drop');
  let skip = limitOf(limit, 'drop', src);
  return lazy(src, function* (v) { if (skip > 0) { skip--; return; } yield v; });
}

function iterFlatMap(this: unknown, f: unknown) {
  const src = direct(this, 'flatMap'); needFn(f, 'flatMap', src);
  return lazy(src, function* (v, i) {
    const inner = f(v, i);
    if ((typeof inner !== 'object' && typeof inner !== 'function') || inner === null) throw new TypeError('flatMap: нужен итератор или перебираемое');
    const method = (inner as { [Symbol.iterator]?: () => Iterator<unknown> })[Symbol.iterator];
    const it = method === undefined ? inner as Iterator<unknown> : method.call(inner);
    // Ушёл потребитель — `yield*` закрывает внутренний, `lazy` — внешний.
    yield* { [Symbol.iterator]: () => it };
  });
}

/** Жадный обход до конца или до `true` от обработчика; и то и отказ обработчика закрывают источник. */
function each(src: Direct, visit: (value: unknown, index: number) => boolean | void): boolean {
  for (let index = 0; ; index++) {
    const r = src.next();
    if (r.done) return false;
    let stop: boolean | void;
    try { stop = visit(r.value, index); } catch (e) { fail(src, e); }
    if (stop) { close(src.it); return true; }
  }
}

function iterReduce(this: unknown, f: unknown, ...initial: unknown[]) {
  const src = direct(this, 'reduce'); needFn(f, 'reduce', src);
  let acc = initial[0], shift = 0;
  if (initial.length === 0) {
    const first = src.next();
    if (first.done) throw new TypeError('reduce: пустой итератор без начального значения');
    acc = first.value;
    shift = 1;
  }
  each(src, (v, i) => { acc = f(acc, v, i + shift); });
  return acc;
}

function iterToArray(this: unknown) {
  const out: unknown[] = [];
  each(direct(this, 'toArray'), v => { out.push(v); });
  return out;
}

function iterForEach(this: unknown, f: unknown) {
  const src = direct(this, 'forEach'); needFn(f, 'forEach', src);
  each(src, (v, i) => { f(v, i); });
}

function iterSome(this: unknown, f: unknown) {
  const src = direct(this, 'some'); needFn(f, 'some', src);
  return each(src, (v, i) => !!f(v, i));
}

function iterEvery(this: unknown, f: unknown) {
  const src = direct(this, 'every'); needFn(f, 'every', src);
  return !each(src, (v, i) => !f(v, i));
}

function iterFind(this: unknown, f: unknown) {
  const src = direct(this, 'find'); needFn(f, 'find', src);
  let found: unknown;
  each(src, (v, i) => { if (!f(v, i)) return false; found = v; return true; });
  return found;
}

/** Все помощники прототипа — одно предложение стандарта, а `map`, `some`, `find` зовутся и у массивов. */
const ITERATOR_HELPERS = {
  map: iterMap, filter: iterFilter, take: iterTake, drop: iterDrop, flatMap: iterFlatMap,
  reduce: iterReduce, toArray: iterToArray, forEach: iterForEach, some: iterSome, every: iterEvery, find: iterFind,
};

const setSize = Object.getOwnPropertyDescriptor(Set.prototype, 'size')?.get as (this: Set<unknown>) => number;

/**
 * `Set.prototype.intersection` — из методов `Set` сборка зовёт только его. Аргумент — «подобное
 * множеству» (`size`, `has`, `keys`); обходится меньшее из двух, как в стандарте.
 */
function setIntersection<T>(this: Set<T>, other: unknown): Set<T> {
  const size = setSize.call(this);
  if (typeof other !== 'object' || other === null) throw new TypeError('intersection: нужно множество');
  const { size: rawSize, has, keys } = other as { size: unknown; has: unknown; keys: unknown };
  const otherSize = Number(rawSize);
  if (Number.isNaN(otherSize)) throw new TypeError('intersection: size не число');
  if (Math.trunc(otherSize) < 0) throw new RangeError('intersection: size меньше нуля');
  needFn(has, 'intersection');
  needFn(keys, 'intersection');
  const out = new Set<T>();
  if (size <= otherSize) {
    for (const v of Set.prototype.values.call(this)) if (has.call(other, v)) out.add(v as T);
  } else {
    const it = direct(keys.call(other), 'intersection');
    for (let r = it.next(); !r.done; r = it.next()) {
      const v = (Object.is(r.value, -0) ? 0 : r.value) as T;
      if (Set.prototype.has.call(this, v)) out.add(v);
    }
  }
  return out;
}

/** `Response.bytes` и `Blob.bytes`: тело — `Uint8Array`. */
async function bytes(this: { arrayBuffer(): Promise<ArrayBuffer> }): Promise<Uint8Array> {
  return new Uint8Array(await this.arrayBuffer());
}

async function* readAll(reader: ReadableStreamDefaultReader<unknown>, preventCancel: boolean): AsyncGenerator<unknown, undefined> {
  let finished = false;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) { finished = true; return undefined; }
      yield value;
    }
  } catch (e) {
    finished = true;
    throw e;
  } finally {
    // Потребитель ушёл раньше (`break`) — поток отменяется, если не просили иного.
    if (!finished && !preventCancel) await reader.cancel();
    reader.releaseLock();
  }
}

/** `ReadableStream.values()` — он же `for await`: поток блокируется сразу, как в стандарте. */
function streamValues(this: ReadableStream<unknown>, options?: { preventCancel?: boolean }) {
  return readAll(this.getReader(), !!options?.preventCancel);
}

/** Сами заплатки — для теста: где функция у среды своя, установленная не видна. */
export const IMPLS = {
  getOrInsert, getOrInsertComputed, sumPrecise, promiseTry, toBase64, fromBase64, toHex,
  iteratorCtor, ...ITERATOR_HELPERS, setIntersection, bytes, streamValues,
};

/** Где заплатка ставится помощникам итератора — для теста. */
export const ITERATOR_PROTOTYPE = ITERATOR_PROTO;

export function installPdfPolyfills(): void {
  for (const proto of [Map.prototype, WeakMap.prototype]) {
    patch(proto, 'getOrInsert', getOrInsert);
    patch(proto, 'getOrInsertComputed', getOrInsertComputed);
  }
  patch(Math, 'sumPrecise', sumPrecise);
  patch(Promise, 'try', promiseTry);
  patch(Uint8Array.prototype, 'toBase64', toBase64);
  patch(Uint8Array.prototype, 'toHex', toHex);
  patch(Uint8Array, 'fromBase64', fromBase64);
  patch(globalThis, 'Iterator', iteratorCtor);
  for (const [name, impl] of Object.entries(ITERATOR_HELPERS)) patch(ITERATOR_PROTO, name, impl);
  patch(Set.prototype, 'intersection', setIntersection);
  // Веб-API — где они есть: в главном потоке и в worker'е есть все три, в Node — тоже.
  if (typeof Response !== 'undefined') patch(Response.prototype, 'bytes', bytes);
  if (typeof Blob !== 'undefined') patch(Blob.prototype, 'bytes', bytes);
  if (typeof ReadableStream !== 'undefined') {
    patch(ReadableStream.prototype, 'values', streamValues);
    patch(ReadableStream.prototype, Symbol.asyncIterator, streamValues);
  }
}

installPdfPolyfills();
