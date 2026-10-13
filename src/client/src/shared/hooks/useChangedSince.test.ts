import { describe, expect, it } from 'vitest';
import { recorded } from './useChangedSince';

describe('запись значения для сравнения', () => {
  it('порядок ключей объекта различием не считается', () => {
    expect(recorded({ a: 1, b: { d: 1, c: 2 } })).toBe(recorded({ b: { c: 2, d: 1 }, a: 1 }));
  });

  it('порядок элементов массива — считается', () => {
    expect(recorded([1, 2])).not.toBe(recorded([2, 1]));
  });

  // `JSON.stringify(new Set([1]))` — это `{}` при любом содержимом: признак правок молчал бы всегда.
  it('Set и Map различаются по содержимому', () => {
    expect(recorded(new Set(['a']))).not.toBe(recorded(new Set()));
    expect(recorded(new Set(['a']))).not.toBe(recorded(new Set(['b'])));
    expect(recorded(new Map([['a', 1]]))).not.toBe(recorded(new Map([['a', 2]])));
  });

  it('значение undefined записывается, а не роняет сравнение', () => {
    expect(recorded(undefined)).toBe(recorded(undefined));
    expect(recorded(undefined)).not.toBe(recorded(null));
  });
});
