/**
 * Заплатки функций, которые вызывает сборка pdf.js 6 и которых нет у браузеров на границе поддержки
 * просмотрщика — Chrome 126, Safari 18 (D5 зонтика viewer, поправка от 25.10.2026).
 *
 * Без них просмотрщик в Chrome 126–146 и Safari 18–26.1 падал бы при запуске, а не показывал отказ:
 * `Map.getOrInsertComputed` есть с Chrome 145 и Safari 26.2, `Math.sumPrecise` — с Chrome 147.
 * Подключается ДО pdf.js дважды — в главном потоке (`pdfEngine.ts`) и во входе worker'а
 * (`pdfWorker.ts`): глобалов главного потока worker не видит.
 *
 * Функцию, которая у браузера есть, заплатка не трогает. Сторож полноты — тест настоящего движка в
 * Node 22 из CI (`pdfEngine.test.ts`): этих функций там нет, а по набору функций Node 22 не новее
 * Chrome 126. Обновлённый pdf.js, вызвавший новую функцию без заплатки, покраснит его.
 */

/** Ставит `impl` под именем `name`, если его нет. `true` — поставлена. */
export function patch(target: object, name: string, impl: unknown): boolean {
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

/** Сами заплатки — для теста: где функция у среды своя, установленная не видна. */
export const IMPLS = { getOrInsert, getOrInsertComputed, sumPrecise, promiseTry, toBase64, fromBase64, toHex };

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
}

installPdfPolyfills();
