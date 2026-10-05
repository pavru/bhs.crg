import { afterEach, describe, expect, it } from 'vitest';
import {
  formatAmount, formatCount, formatDate, formatDateTime, formatDayTime, formatInput, formatInputAmount,
  formatLocale, formatMoney, formatNumber, formatQuantity, formatTime, setFormatLocale,
} from './format';

// Пробел между разрядами и перед знаком валюты у русского языка — неразрывный.
const NB = ' ';

afterEach(() => setFormatLocale('ru-RU'));

describe('числа', () => {
  it('сумма — с копейками и знаком валюты, даже круглая', () => {
    expect(formatMoney(1234.5)).toBe(`1${NB}234,50${NB}₽`);
    expect(formatMoney(100)).toBe(`100,00${NB}₽`);
    expect(formatMoney(0)).toBe(`0,00${NB}₽`);
  });

  it('сумма без знака валюты — те же копейки', () => {
    expect(formatAmount(412500)).toBe(`412${NB}500,00`);
  });

  it('количество — без дописанных нулей и не точнее трёх знаков', () => {
    expect(formatQuantity(1250)).toBe(`1${NB}250`);
    expect(formatQuantity(7.25)).toBe('7,25');
    expect(formatQuantity(0.12345)).toBe('0,123');
  });

  it('счётчик — целое с разрядами', () => {
    expect(formatCount(12480)).toBe(`12${NB}480`);
  });

  it('число таблицы — не точнее двух знаков, пока не попросили иначе', () => {
    expect(formatNumber(1.005)).toBe('1,01');
    expect(formatNumber(1.23456, 4)).toBe('1,2346');
  });
});

describe('число в поле ввода', () => {
  it('без разрядов: пробел в поле мешает править', () => {
    expect(formatInput(1250.5)).toBe('1250,5');
    expect(formatInputAmount(1250.5)).toBe('1250,50');
  });

  it('пустое остаётся пустым, а не нулём', () => {
    expect(formatInput(null)).toBe('');
    expect(formatInput(undefined)).toBe('');
    expect(formatInputAmount(null)).toBe('');
  });

  it('значение из базы не округляется: иначе сохранение записало бы округлённое', () => {
    expect(formatInput(12.34567)).toBe('12,34567');
    expect(formatInput(0.1 + 0.2)).toBe('0,3');
  });

  it('расчётное число округляется до названного предела', () => {
    expect(formatInput(7.2549, 3)).toBe('7,255');
  });
});

describe('даты', () => {
  it('календарный день не сдвигается поясом', () => {
    // `new Date('2026-10-05')` — полночь по Гринвичу; западнее Гринвича это ещё четвёртое число.
    expect(formatDate('2026-10-05')).toBe('05.10.2026');
    expect(formatDate('2026-01-01')).toBe('01.01.2026');
  });

  it('момент показан в поясе браузера', () => {
    const moment = new Date(2026, 9, 5, 14, 32, 7);
    expect(formatDate(moment)).toBe('05.10.2026');
    expect(formatDateTime(moment)).toBe('05.10.2026, 14:32');
    expect(formatDayTime(moment)).toBe('05.10, 14:32');
    expect(formatTime(moment)).toBe('14:32');
  });

  it('нечитаемое возвращается как есть, а не «Invalid Date»', () => {
    expect(formatDate('вчера')).toBe('вчера');
    expect(formatDateTime('')).toBe('');
  });
});

describe('язык форматирования', () => {
  it('смена языка меняет всё сразу — число, сумму и дату', () => {
    setFormatLocale('en-US');

    expect(formatLocale()).toBe('en-US');
    expect(formatQuantity(1250.5)).toBe('1,250.5');
    expect(formatMoney(1234.5)).toBe('₽1,234.50');
    expect(formatDate('2026-10-05')).toBe('10/05/2026');
    expect(formatInput(1250.5)).toBe('1250.5');
  });

  it('вернувшись, язык не оставляет за собой прежних форматтеров', () => {
    setFormatLocale('de-DE');
    expect(formatQuantity(1250.5)).toBe('1.250,5');

    setFormatLocale('ru-RU');
    expect(formatQuantity(1250.5)).toBe(`1${NB}250,5`);
  });
});
