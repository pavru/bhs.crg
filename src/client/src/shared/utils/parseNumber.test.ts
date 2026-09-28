import { describe, it, expect } from 'vitest';
import { parseNumber } from './parseNumber';

/** Таблица из issue #1064, разбор по русской локали. `null` = отказ (значение НЕ число целиком). */
const RU_TABLE: [raw: string, expected: number | null][] = [
  ['1.234,56', 1234.56],      // разделитель тысяч точкой — обычный вид в выгрузках и PDF
  ['1 234,56', 1234.56],      // разделитель тысяч пробелом
  ['12 шт', null],            // количество с единицей измерения — не число
  ['12,5', 12.5],             // запятая как десятичный разделитель
  ['abc', null],
  ['1.234.567,89', 1234567.89], // смешанная запись
  ['-1 234,56', -1234.56],    // отрицательное
];

describe('parseNumber, локаль ru-RU', () => {
  it.each(RU_TABLE)('%s → %s', (raw, expected) => {
    expect(parseNumber(raw, 'ru-RU')).toBe(expected);
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
    const broken = RU_TABLE.filter(([raw, expected]) => naive(raw) !== expected).map(([raw]) => raw);
    expect(broken).toEqual(['1.234,56', '12 шт', '1.234.567,89']);
  });

  it('дробная часть любой длины — в том числе три знака', () => {
    // Три знака после запятой группировкой быть не могут: первая группа — 1–3 цифры.
    expect(parseNumber('0,125', 'ru-RU')).toBe(0.125);
    expect(parseNumber('-0,125', 'ru-RU')).toBe(-0.125);
    expect(parseNumber('1000,125', 'ru-RU')).toBe(1000.125);
    expect(parseNumber('12345,678', 'ru-RU')).toBe(12345.678);
    expect(parseNumber(',5', 'ru-RU')).toBe(0.5);
  });

  it('целые с группировкой и без', () => {
    expect(parseNumber('12', 'ru-RU')).toBe(12);
    expect(parseNumber('1 234 567', 'ru-RU')).toBe(1234567);
    expect(parseNumber('+42', 'ru-RU')).toBe(42);
    expect(parseNumber('-42', 'ru-RU')).toBe(-42);
  });

  it('неразрывный пробел Excel считается разделителем тысяч', () => {
    expect(parseNumber('1\u00a0234,56', 'ru-RU')).toBe(1234.56);
    expect(parseNumber('1\u202f234\u202f567,89', 'ru-RU')).toBe(1234567.89);
  });

  it('два прочтения — отказ, одно — ответ', () => {
    // `1.234` это и «тысяча двести тридцать четыре» (точка группирует), и «1,234» (точка
    // десятичная). Догадка здесь и есть дефект, поэтому отказ.
    expect(parseNumber('1.234', 'ru-RU')).toBeNull();
    expect(parseNumber('123.456', 'ru-RU')).toBeNull();
    // А эти записи читаются единственным способом: группировкой они быть не могут (группа не из
    // трёх цифр либо первая группа длиннее трёх), значит точка в них десятичная.
    expect(parseNumber('12.5', 'ru-RU')).toBe(12.5);
    expect(parseNumber('0.75', 'ru-RU')).toBe(0.75);
    expect(parseNumber('1234.567', 'ru-RU')).toBe(1234.567);
    // Здесь наоборот: дробной части быть не может, читается только как разделители тысяч.
    expect(parseNumber('1.234.567', 'ru-RU')).toBe(1234567);
  });

  it('битая группировка — отказ', () => {
    expect(parseNumber('1 23 456', 'ru-RU')).toBeNull();
    expect(parseNumber('1.23.456,7', 'ru-RU')).toBeNull();
    expect(parseNumber('1234.5678,9', 'ru-RU')).toBeNull();
    expect(parseNumber('1,2,3', 'ru-RU')).toBeNull();
  });

  it('мусор рядом с числом не отбрасывается', () => {
    expect(parseNumber('12 м²', 'ru-RU')).toBeNull();
    expect(parseNumber('~12', 'ru-RU')).toBeNull();
    expect(parseNumber('12%', 'ru-RU')).toBeNull();
    expect(parseNumber('1e3', 'ru-RU')).toBeNull();
    expect(parseNumber('12,', 'ru-RU')).toBeNull();
    expect(parseNumber('-', 'ru-RU')).toBeNull();
    expect(parseNumber('', 'ru-RU')).toBeNull();
    expect(parseNumber('   ', 'ru-RU')).toBeNull();
  });
});

describe('parseNumber, локаль en-US', () => {
  it('десятичный разделитель — точка, группирующий — запятая', () => {
    expect(parseNumber('12.5', 'en-US')).toBe(12.5);
    expect(parseNumber('1.234', 'en-US')).toBe(1.234);
    expect(parseNumber('1,234.56', 'en-US')).toBe(1234.56);
    expect(parseNumber('1,234,567.89', 'en-US')).toBe(1234567.89);
    expect(parseNumber('1 234.56', 'en-US')).toBe(1234.56);
  });

  it('чужая запись читается, пока читается однозначно', () => {
    // Запятая как разделитель тысяч этих записей не объясняет, десятичная — объясняет, и другого
    // прочтения нет. А `1,234` объясняется обоими способами, поэтому отказ.
    expect(parseNumber('12,5', 'en-US')).toBe(12.5);
    expect(parseNumber('1 234,56', 'en-US')).toBe(1234.56);
    expect(parseNumber('1.234,56', 'en-US')).toBe(1234.56);
    expect(parseNumber('1,234', 'en-US')).toBe(1234);     // по локали — разделитель тысяч, шаг 1
  });
});

describe('parseNumber, локаль de-DE', () => {
  it('точка группирует, запятая отделяет дробную часть', () => {
    expect(parseNumber('1.234,56', 'de-DE')).toBe(1234.56);
    expect(parseNumber('1.234', 'de-DE')).toBe(1234);     // здесь точка — СВОЙ групповой знак
    expect(parseNumber('12,5', 'de-DE')).toBe(12.5);
    expect(parseNumber('1,234.56', 'de-DE')).toBe(1234.56); // единственное прочтение — английское
  });
});
