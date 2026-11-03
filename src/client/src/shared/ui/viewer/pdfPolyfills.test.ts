import { describe, expect, it } from 'vitest';
import { IMPLS, ITERATOR_PROTOTYPE, patch } from './pdfPolyfills';

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

  it('после установки нужное pdf.js есть', () => {
    const fn = (o: object, name: string) => typeof (o as Record<string, unknown>)[name];
    expect(fn(Map.prototype, 'getOrInsertComputed')).toBe('function');
    expect(fn(WeakMap.prototype, 'getOrInsert')).toBe('function');
    expect(fn(Math, 'sumPrecise')).toBe('function');
    expect(fn(Promise, 'try')).toBe('function');
    expect(fn(Uint8Array.prototype, 'toBase64')).toBe('function');
    expect(fn(Uint8Array, 'fromBase64')).toBe('function');
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
    expect(Object.getPrototypeOf(Object.getPrototypeOf(Object.getPrototypeOf(filtered)))).toBe(ITERATOR_PROTOTYPE);
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
  const values = (s: ReadableStream<string>, options?: object) =>
    (IMPLS.streamValues as (this: ReadableStream<string>, o?: object) => AsyncIterable<string>).call(s, options);

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

  it('после установки нужное pdf.js из пробелов Firefox и Safari есть', () => {
    expect(typeof (globalThis as { Iterator?: unknown }).Iterator).toBe('function');
    for (const name of ['map', 'filter', 'take', 'drop', 'flatMap', 'reduce', 'toArray', 'forEach', 'some', 'every', 'find']) expect(typeof native(name)).toBe('function');
    expect(typeof (Set.prototype as unknown as Record<string, unknown>).intersection).toBe('function');
    expect(typeof (Response.prototype as unknown as Record<string, unknown>).bytes).toBe('function');
    expect(typeof (ReadableStream.prototype as unknown as Record<symbol, unknown>)[Symbol.asyncIterator]).toBe('function');
  });
});
