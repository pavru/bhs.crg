import { describe, it, expect } from 'vitest';
import { toCamelKey } from './schemaConstants';

describe('toCamelKey', () => {
  it('склеивает слова, каждое с заглавной буквы', () => {
    expect(toCamelKey('Номер документа')).toBe('НомерДокумента');
  });

  it('схлопывает несколько пробелов/пунктуацию в один разделитель', () => {
    expect(toCamelKey('Номер  документа, №1')).toBe('НомерДокумента1');
  });

  it('оставляет одно слово как есть (кроме регистра первой буквы)', () => {
    expect(toCamelKey('артикул')).toBe('Артикул');
  });

  it('пустая строка → пустой ключ', () => {
    expect(toCamelKey('')).toBe('');
  });

  it('обрезает начальные/конечные разделители', () => {
    expect(toCamelKey('  Дата  ')).toBe('Дата');
  });
});
