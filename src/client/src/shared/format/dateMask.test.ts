import { afterEach, describe, expect, it, vi } from 'vitest';
import { dateMask, firstDayOfWeek, monthNames, weekdayNames } from './dateMask';

// Маска даты по языку пользователя (issue #1461, решение владельца 18.10.2026): таблица из задачи.
describe('dateMask: язык × точность', () => {
  it.each([
    ['ru-RU', 'ДД.ММ.ГГГГ', 'ММ.ГГГГ', 'ГГГГ'],
    ['en-US', 'MM/DD/YYYY', 'MM/YYYY', 'YYYY'],
    ['en-GB', 'DD/MM/YYYY', 'MM/YYYY', 'YYYY'],
    ['de-DE', 'TT.MM.JJJJ', 'MM.JJJJ', 'JJJJ'],
  ] as const)('%s: %s, %s, %s', (locale, day, month, year) => {
    expect(dateMask(locale, 'day').text).toBe(day);
    expect(dateMask(locale, 'month').text).toBe(month);
    expect(dateMask(locale, 'year').text).toBe(year);
  });

  it('порядок — у Intl: en-US начинает с месяца, ru-RU — с дня', () => {
    expect(dateMask('en-US').order).toEqual(['m', 'd', 'y']);
    expect(dateMask('ru-RU').order).toEqual(['d', 'm', 'y']);
  });

  it('язык без своих обозначений — английские, порядок всё равно его', () => {
    const mask = dateMask('fr-FR');
    expect(mask.labels).toEqual({ d: 'DD', m: 'MM', y: 'YYYY' });
    expect(mask.order).toEqual(['d', 'm', 'y']);
  });
});

describe('календарь: названия и первый день недели', () => {
  afterEach(() => vi.restoreAllMocks());

  it('месяцы и дни недели — словами языка, неделя — с его первого дня', () => {
    expect(monthNames('ru-RU', 'long')[9]).toBe('Октябрь');
    expect(monthNames('de-DE', 'short')[9]).toBe('Okt');
    expect(weekdayNames('en-US')[0]).toBe('Sun');
    expect(weekdayNames('ru-RU')[0]).toBe('Пн');
    expect(firstDayOfWeek('en-US')).toBe(7);
    expect(firstDayOfWeek('en-GB')).toBe(1);
  });

  it('браузер без getWeekInfo (Firefox): первый день — по региону языка', () => {
    const proto = Intl.Locale.prototype as unknown as Record<string, unknown>;
    const saved = Object.getOwnPropertyDescriptor(proto, 'getWeekInfo');
    const savedInfo = Object.getOwnPropertyDescriptor(proto, 'weekInfo');
    delete proto.getWeekInfo;
    delete proto.weekInfo;
    try {
      expect((new Intl.Locale('en-US') as unknown as Record<string, unknown>).getWeekInfo).toBeUndefined();
      expect(firstDayOfWeek('en-US')).toBe(7);
      expect(firstDayOfWeek('ru-RU')).toBe(1);
      expect(firstDayOfWeek('de')).toBe(1);
    } finally {
      if (saved) Object.defineProperty(proto, 'getWeekInfo', saved);
      if (savedInfo) Object.defineProperty(proto, 'weekInfo', savedInfo);
    }
  });
});
