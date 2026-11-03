import { describe, expect, it } from 'vitest';
import { IMPLS, installPdfPolyfills, ITERATOR_HELPER_NAMES, ITERATOR_PROTOTYPE, patch } from './pdfPolyfills';

/** Заплатки функций pdf.js 6 для браузеров на границе поддержки (D5 зонтика viewer). */

describe('заплатки pdf.js', () => {
  it('ставятся только туда, где функции нет', () => {
    const own = () => 'своя';
    const target: Record<string, unknown> = { f: own };
    expect(patch(target, 'f', () => 'чужая')).toBe(false);
    expect(target.f).toBe(own);
    expect(patch(target, 'g', own)).toBe(true);
    expect(Object.keys(target)).toEqual(['f']); // не перечисляется, как у настоящих
  });

  // На Node, где функции свои, проверка «после установки функция есть» зеленела бы и без заплатки:
  // поэтому функция убирается, установка повторяется — и на месте обязана оказаться заплатка.
  const places: [string, object, PropertyKey, unknown][] = [
    ['Map.prototype.getOrInsert', Map.prototype, 'getOrInsert', IMPLS.getOrInsert],
    ['Map.prototype.getOrInsertComputed', Map.prototype, 'getOrInsertComputed', IMPLS.getOrInsertComputed],
    ['WeakMap.prototype.getOrInsert', WeakMap.prototype, 'getOrInsert', IMPLS.getOrInsert],
    ['WeakMap.prototype.getOrInsertComputed', WeakMap.prototype, 'getOrInsertComputed', IMPLS.getOrInsertComputed],
    ['Math.sumPrecise', Math, 'sumPrecise', IMPLS.sumPrecise],
    ['Promise.try', Promise, 'try', IMPLS.promiseTry],
    ['Uint8Array.prototype.toBase64', Uint8Array.prototype, 'toBase64', IMPLS.toBase64],
    ['Uint8Array.prototype.toHex', Uint8Array.prototype, 'toHex', IMPLS.toHex],
    ['Uint8Array.fromBase64', Uint8Array, 'fromBase64', IMPLS.fromBase64],
    ['globalThis.Iterator', globalThis, 'Iterator', IMPLS.iteratorCtor],
    ...ITERATOR_HELPER_NAMES.map(name => [`%IteratorPrototype%.${name}`, ITERATOR_PROTOTYPE, name, IMPLS[name]] as [string, object, PropertyKey, unknown]),
    ['Set.prototype.intersection', Set.prototype, 'intersection', IMPLS.setIntersection],
    ['Response.prototype.bytes', Response.prototype, 'bytes', IMPLS.bytes],
    ['Blob.prototype.bytes', Blob.prototype, 'bytes', IMPLS.bytes],
    ['ReadableStream.prototype.values', ReadableStream.prototype, 'values', IMPLS.streamValues],
    ['ReadableStream.prototype[Symbol.asyncIterator]', ReadableStream.prototype, Symbol.asyncIterator, IMPLS.streamValues],
  ];

  it.each(places)('установка ставит заплатку туда, где функции нет: %s', (_, target, name, impl) => {
    const own = Object.getOwnPropertyDescriptor(target, name);
    try {
      Reflect.deleteProperty(target, name);
      installPdfPolyfills();
      expect((target as Record<PropertyKey, unknown>)[name]).toBe(impl);
    } finally {
      Reflect.deleteProperty(target, name);
      if (own) Object.defineProperty(target, name, own);
    }
  });

  it('getOrInsert(Computed): существующее не перезаписывается, функция зовётся один раз', () => {
    const m = new Map<string, number[]>([['a', [1]]]);
    let calls = 0;
    const make = () => { calls++; return [] as number[]; };
    expect(IMPLS.getOrInsertComputed.call(m, 'a', make)).toEqual([1]);
    (IMPLS.getOrInsertComputed.call(m, 'b', make) as number[]).push(2);
    expect(IMPLS.getOrInsertComputed.call(m, 'b', make)).toEqual([2]);
    expect(calls).toBe(1);
    expect(IMPLS.getOrInsert.call(m, 'c', [3])).toEqual([3]);
  });

  it('sumPrecise не копит ошибку округления', () => {
    expect(IMPLS.sumPrecise([1e20, 0.1, -1e20])).toBe(0.1);
    expect(IMPLS.sumPrecise([0.1, 0.2, 0.3])).toBe(0.6);
    expect(IMPLS.sumPrecise([3, 4, 5])).toBe(12);
    expect(Object.is(IMPLS.sumPrecise([]), -0)).toBe(true);
    expect(IMPLS.sumPrecise([Infinity, 1])).toBe(Infinity);
    expect(IMPLS.sumPrecise([Infinity, -Infinity])).toBeNaN();
    expect(() => IMPLS.sumPrecise(['1' as unknown as number])).toThrow(TypeError);
  });

  it('Promise.try ловит синхронный бросок', async () => {
    await expect(IMPLS.promiseTry(() => { throw new Error('x'); })).rejects.toThrow('x');
    await expect(IMPLS.promiseTry((a: unknown) => a, 5)).resolves.toBe(5);
  });

  it('base64 и hex — туда и обратно', () => {
    const bytes = new Uint8Array([0, 251, 255, 65]);
    const b64 = IMPLS.toBase64.call(bytes);
    expect(b64).toBe('APv/QQ==');
    expect(IMPLS.toBase64.call(bytes, { alphabet: 'base64url', omitPadding: true })).toBe('APv_QQ');
    expect([...IMPLS.fromBase64(b64)]).toEqual([...bytes]);
    expect([...IMPLS.fromBase64('APv_QQ', { alphabet: 'base64url' })]).toEqual([...bytes]);
    expect(IMPLS.toHex.call(bytes)).toBe('00fbff41');
    expect(() => IMPLS.fromBase64('**')).toThrow(SyntaxError);
  });
});

/**
 * Итераторные помощники, методы `Set`, `bytes`, обход потока — пробелы Firefox 126 и Safari 18
 * (change sharedui-pdf-border-gaps). Эталон — встроенные функции Node той же версии, что в CI.
 */
describe('заплатки границы Firefox и Safari', () => {
  type Helper = (this: unknown, ...args: unknown[]) => unknown;
  const native = (name: string) => (ITERATOR_PROTOTYPE as Record<string, Helper>)[name];
  const shim = (name: string) => (IMPLS as unknown as Record<string, Helper>)[name];

  /** Итератор, который считает свои `return`. */
  function counted(values: number[]) {
    const it = values[Symbol.iterator]();
    const box = { closed: 0, it: Object.assign(Object.create(ITERATOR_PROTOTYPE), {
      next: () => it.next(),
      return: () => { box.closed++; return { done: true, value: undefined }; },
    }) as Iterator<number> };
    return box;
  }

  /** Вызов на свежем итераторе: итог, закрыт ли источник, отказ. */
  function run(impl: Helper, values: number[], args: unknown[], consume = (r: unknown) => r) {
    const src = counted(values);
    try {
      const r = consume(impl.call(src.it, ...args));
      return { r, closed: src.closed };
    } catch (e) {
      return { error: (e as Error).constructor.name, closed: src.closed };
    }
  }

  const all = (r: unknown) => [...(r as Iterable<unknown>)];
  const two = (r: unknown) => { const it = r as Iterator<unknown>; const out = [it.next().value, it.next().value]; it.return?.(); return out; };
  const odd = (v: unknown) => (v as number) % 2 === 1;
  const boom = () => { throw new Error('x'); };
  const cases: [string, unknown[], ((r: unknown) => unknown)?][] = [
    ['map', [(v: number, i: number) => v * 10 + i], all], ['map', [(v: number) => v], two], ['map', [boom], all], ['map', ['не функция'], all],
    ['filter', [odd], all], ['filter', [odd], two],
    ['take', [2], all], ['take', [0], all], ['take', [Infinity], all], ['take', [-1], all], ['take', [NaN], all],
    ['drop', [2], all], ['drop', [9], all], ['drop', [-1], all],
    ['flatMap', [(v: number) => [v, v]], all], ['flatMap', [(v: number) => [v, v]], two], ['flatMap', [(v: number) => v], all],
    ['reduce', [(a: number, v: number, i: number) => a + v * i]], ['reduce', [(a: number, v: number) => a + v, 100]], ['reduce', [boom]],
    ['toArray', []], ['forEach', [boom]],
    ['some', [odd]], ['some', [() => false]], ['every', [odd]], ['every', [() => true]],
    ['find', [(v: number) => v > 2]], ['find', [() => false]], ['find', [boom]],
  ];

  it.each(cases)('%s(%j) — как встроенный', (name, args, consume = r => r) => {
    expect(run(shim(name), [1, 2, 3, 4], args, consume)).toEqual(run(native(name), [1, 2, 3, 4], args, consume));
  });

  it('reduce пустого без начального — TypeError, как у встроенного', () => {
    expect(run(shim('reduce'), [], [(a: number) => a])).toEqual(run(native('reduce'), [], [(a: number) => a]));
  });

  it('цепочка pdf.js: keys().filter(…).toArray() и values().some(…)', () => {
    const set = new Set([1, 2, 3]);
    const filtered = shim('filter').call(set.keys(), odd);
    expect(shim('toArray').call(filtered)).toEqual([1, 3]);
    expect(shim('some').call(new Map([['a', 1]]).values(), (v: unknown) => v === 1)).toBe(true);
    // Ленивый итог сам наследует общий прототип: на нём следующий помощник.
    expect(Object.prototype.isPrototypeOf.call(ITERATOR_PROTOTYPE, filtered as object)).toBe(true);
    expect(Object.prototype.toString.call(filtered)).toBe(Object.prototype.toString.call(native('filter').call(set.keys(), odd)));
  });

  it.each(['map', 'filter', 'take', 'drop', 'flatMap'])('%s: return() до первого next() закрывает источник, как встроенный', name => {
    const arg = name === 'take' || name === 'drop' ? 1 : (v: number) => (name === 'flatMap' ? [v] : v);
    const early = (r: unknown) => { const it = r as Iterator<unknown>; const out = [it.return?.(), it.next()]; return out; };
    expect(run(shim(name), [1, 2], [arg], early)).toEqual(run(native(name), [1, 2], [arg], early));
  });

  it('предел и size приводятся к числу по стандарту: BigInt — TypeError, как у встроенного', () => {
    for (const name of ['take', 'drop']) expect(run(shim(name), [1], [2n], all)).toEqual(run(native(name), [1], [2n], all));
    const impl = IMPLS.setIntersection as (this: Set<unknown>, other: unknown) => Set<unknown>;
    const nativeImpl = (Set.prototype as unknown as { intersection: typeof impl }).intersection;
    const like = { size: 2n, has: () => true, keys: () => [][Symbol.iterator]() };
    expect(() => nativeImpl.call(new Set([1]), like)).toThrow(TypeError);
    expect(() => impl.call(new Set([1]), like)).toThrow(TypeError);
  });

  it('next() вернул не объект — TypeError, как у встроенного, а не значение undefined', () => {
    const bad = () => Object.assign(Object.create(ITERATOR_PROTOTYPE), { next: () => 5 }) as Iterator<number>;
    for (const name of ['toArray', 'map']) {
      const consume = name === 'map' ? all : (r: unknown) => r;
      const args = name === 'map' ? [(v: unknown) => v] : [];
      const outcome = (impl: Helper) => { try { return consume(impl.call(bad(), ...args)); } catch (e) { return (e as Error).constructor.name; } };
      expect(outcome(shim(name))).toBe('TypeError');
      expect(outcome(shim(name))).toEqual(outcome(native(name)));
    }
  });

  it('глобал Iterator — абстрактный, prototype — общий прототип итераторов', () => {
    const Ctor = IMPLS.iteratorCtor as unknown as { new(): unknown; (): unknown; prototype: object; name: string };
    expect(Ctor.prototype).toBe(ITERATOR_PROTOTYPE);
    expect(Ctor.name).toBe('Iterator');
    expect(() => Ctor()).toThrow(TypeError);
    expect(() => new Ctor()).toThrow(TypeError);
    class Own extends (Ctor as unknown as new () => object) {}
    expect(new Own()).toBeInstanceOf(Own);
  });

  it('Set.intersection — как встроенный, в обе стороны и с «подобным множеству»', () => {
    const impl = IMPLS.setIntersection as (this: Set<unknown>, other: unknown) => Set<unknown>;
    const nativeImpl = (Set.prototype as unknown as { intersection: typeof impl }).intersection;
    const a = new Set([5, 1, 3, -0]);
    const like = { size: 2, has: (v: unknown) => v === 3 || v === 0, keys: () => [0, 3][Symbol.iterator]() };
    for (const other of [new Set([3, 1, 9]), new Set([3, 0, 7, 8, 9, 10]), like]) {
      expect([...impl.call(a, other)]).toEqual([...nativeImpl.call(a, other)]);
    }
    expect(() => impl.call(a, [1])).toThrow(TypeError);
    expect(() => impl.call(a, { size: -1, has() {}, keys() {} })).toThrow(RangeError);
    expect(() => impl.call([] as unknown as Set<unknown>, new Set())).toThrow(TypeError);
  });

  it('bytes — Uint8Array тела, у Response и у Blob', async () => {
    const impl = IMPLS.bytes as (this: Body | Blob) => Promise<Uint8Array>;
    expect([...await impl.call(new Response(new Uint8Array([1, 2, 250])))]).toEqual([1, 2, 250]);
    expect([...await impl.call(new Blob([new Uint8Array([7])]))]).toEqual([7]);
    await expect(impl.call({} as Blob)).rejects.toThrow(TypeError);
  });

  function stream(chunks: string[]) {
    let cancelled: unknown = null;
    const s = new ReadableStream<string>({
      start(c) { for (const chunk of chunks) c.enqueue(chunk); c.close(); },
      cancel(reason) { cancelled = reason ?? 'отменён'; },
    });
    return { s, cancelled: () => cancelled };
  }
  type Values = (this: ReadableStream<string>, o?: object) => AsyncIterable<string>;
  const values = (s: ReadableStream<string>, options?: object) =>
    (IMPLS.streamValues as unknown as Values).call(s, options);

  it('обход потока: все куски, затем поток свободен', async () => {
    const { s } = stream(['a', 'b']);
    const out: string[] = [];
    for await (const chunk of values(s)) out.push(chunk);
    expect(out).toEqual(['a', 'b']);
    expect(s.locked).toBe(false);
  });

  it('обход потока: ранний выход отменяет поток, preventCancel — нет', async () => {
    const first = stream(['a', 'b']);
    for await (const chunk of values(first.s)) { expect(chunk).toBe('a'); break; }
    expect(first.cancelled()).not.toBeNull();
    expect(first.s.locked).toBe(false);
    const kept = stream(['a', 'b']);
    for await (const chunk of values(kept.s, { preventCancel: true })) { expect(chunk).toBe('a'); break; }
    expect(kept.cancelled()).toBeNull();
  });

  it('обход потока блокирует его сразу, как встроенный', () => {
    const { s } = stream(['a']);
    values(s);
    expect(s.locked).toBe(true);
    expect(() => values(s)).toThrow(TypeError);
  });

  const nativeValues = (s: ReadableStream<string>, options?: object) =>
    (ReadableStream.prototype as unknown as { values: Values }).values.call(s, options);

  /** Исход раннего ухода: отменён ли поток, свободен ли он, отказ. */
  async function leave(make: typeof values, quit: (it: AsyncIterator<string>) => Promise<unknown>, failingCancel = false) {
    let cancelled = false;
    const s = new ReadableStream<string>({
      start(c) { c.enqueue('a'); },
      cancel() { cancelled = true; if (failingCancel) throw new Error('отмена'); },
    });
    const it = make(s)[Symbol.asyncIterator]();
    let error: string | undefined;
    try { await quit(it); } catch (e) { error = (e as Error).message; }
    return { cancelled, locked: s.locked, error };
  }

  it('обход потока: return() до первого next() отменяет поток и снимает блокировку, как встроенный', async () => {
    const quit = (it: AsyncIterator<string>) => it.return!();
    const shimmed = await leave(values, quit);
    expect(shimmed).toEqual({ cancelled: true, locked: false, error: undefined });
    expect(shimmed).toEqual(await leave(nativeValues, quit));
  });

  it('обход потока: отказ отмены не оставляет поток заблокированным, как у встроенного', async () => {
    const quit = async (it: AsyncIterator<string>) => { await it.next(); await it.return!(); };
    const shimmed = await leave(values, quit, true);
    expect(shimmed).toEqual({ cancelled: true, locked: false, error: 'отмена' });
    expect(shimmed).toEqual(await leave(nativeValues, quit, true));
  });
});
