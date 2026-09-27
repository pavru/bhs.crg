import { describe, it, expect } from 'vitest';
import { parseNumberStrict } from './parseNumber';

/** Таблица из issue #1064: `null` = отказ по ячейке (значение НЕ число целиком). */
const TABLE: [raw: string, expected: number | null][] = [
  ['1.234,56', 1234.56],      // разделитель тысяч точкой — обычный вид в выгрузках и PDF
  ['1 234,56', 1234.56],      // разделитель тысяч пробелом
  ['12 шт', null],            // количество с единицей измерения — не число
  ['12,5', 12.5],             // запятая как десятичный разделитель
  ['abc', null],
  ['1.234.567,89', 1234567.89], // смешанная запись
  ['-1 234,56', -1234.56],    // отрицательное
];

describe('parseNumberStrict', () => {
  it.each(TABLE)('%s → %s', (raw, expected) => {
    expect(parseNumberStrict(raw)).toBe(expected);
  });

  it('нынешний наивный разбор эту таблицу НЕ проходит', () => {
    // Сторож самому сторожу (issue #1064): таблица обязана ловить прежний код
    //   parseFloat(raw.replace(',', '.').replace(/\s/g, ''))
    // иначе она проверяет себя, а не дыру. Прежний разбор менял ПЕРВУЮ запятую и брал префикс:
    // `1.234,56` уезжало в 1.234 (в тысячу раз меньше), `12 шт` проходило как 12.
    const naive = (raw: string) => {
      const n = parseFloat(raw.replace(',', '.').replace(/\s/g, ''));
      return isNaN(n) ? null : n;
    };
    const broken = TABLE.filter(([raw, expected]) => naive(raw) !== expected).map(([raw]) => raw);
    expect(broken).toEqual(['1.234,56', '12 шт', '1.234.567,89']);
  });

  it('точка как десятичный разделитель', () => {
    expect(parseNumberStrict('12.5')).toBe(12.5);
    expect(parseNumberStrict('0.75')).toBe(0.75);
    expect(parseNumberStrict('.5')).toBe(0.5);
  });

  it('английская запись: тысячи запятой, дробная часть точкой', () => {
    expect(parseNumberStrict('1,234.56')).toBe(1234.56);
    expect(parseNumberStrict('1,234,567.89')).toBe(1234567.89);
  });

  it('целые числа с группировкой и без', () => {
    expect(parseNumberStrict('12')).toBe(12);
    expect(parseNumberStrict('1 234 567')).toBe(1234567);
    expect(parseNumberStrict('1.234.567')).toBe(1234567);
    expect(parseNumberStrict('+42')).toBe(42);
    expect(parseNumberStrict('-42')).toBe(-42);
  });

  it('неразрывный пробел Excel считается разделителем тысяч', () => {
    expect(parseNumberStrict('1\u00a0234,56')).toBe(1234.56);
    expect(parseNumberStrict('1\u202f234\u202f567,89')).toBe(1234567.89);
  });

  it('пустое значение — отказ, а не нуль', () => {
    expect(parseNumberStrict('')).toBeNull();
    expect(parseNumberStrict('   ')).toBeNull();
  });

  it('единственный разделитель и ровно три цифры за ним — запись неоднозначная, отказ', () => {
    // `1.234` это и «тысяча двести тридцать четыре», и «1,234». Догадка тут и есть дефект.
    expect(parseNumberStrict('1.234')).toBeNull();
    expect(parseNumberStrict('1,234')).toBeNull();
    // Группы размечены пробелами — тогда точка/запятая однозначно десятичная.
    expect(parseNumberStrict('1 234.567')).toBe(1234.567);
  });

  it('битая группировка — отказ', () => {
    expect(parseNumberStrict('1 23 456')).toBeNull();
    expect(parseNumberStrict('1.23.456')).toBeNull();
    expect(parseNumberStrict('1234.5678,9')).toBeNull();
  });

  it('мусор рядом с числом не отбрасывается', () => {
    expect(parseNumberStrict('12 м²')).toBeNull();
    expect(parseNumberStrict('~12')).toBeNull();
    expect(parseNumberStrict('12%')).toBeNull();
    expect(parseNumberStrict('1e3')).toBeNull();
    expect(parseNumberStrict('12,')).toBeNull();
    expect(parseNumberStrict('-')).toBeNull();
  });
});
