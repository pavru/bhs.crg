import { describe, expect, it } from 'vitest';
import { IMPLS, patch } from './pdfPolyfills';

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
