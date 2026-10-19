import { afterEach, describe, expect, it, vi } from 'vitest';
import { dateMask, firstDayOfWeek, inMask, monthNames, weekdayNames } from './dateMask';

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

  it('имена сегментов — словами языка, для чтения с экрана (issue #1463)', () => {
    expect(dateMask('ru-RU').names).toEqual({ d: 'день', m: 'месяц', y: 'год' });
    expect(dateMask('en-US').names).toEqual({ d: 'day', m: 'month', y: 'year' });
    expect(dateMask('de-DE').names.d).toBe('Tag');
  });

  it('inMask: хранимая дата в записи маски, с точностью; не ISO — как есть', () => {
    expect(inMask('2026-07-01', 'ru-RU')).toBe('01.07.2026');
    expect(inMask('2026-07-01', 'en-US')).toBe('07/01/2026');
    expect(inMask('2026-07-01', 'de-DE', 'month')).toBe('07.2026');
    expect(inMask('2026-07-01', 'en-US', 'year')).toBe('2026');
    expect(inMask('2026-07-01T10:00:00', 'ru-RU')).toBe('01.07.2026');
    expect(inMask('мусор', 'ru-RU')).toBe('мусор');
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

  // Ревью PR #1544: у CLDR сокращения русских месяцев разной длины («февр.», «сент.», «май»).
  it('короткие месяцы: три буквы, если различимы, иначе сокращения языка', () => {
    expect(monthNames('ru-RU', 'short')).toEqual(
      ['Янв', 'Фев', 'Мар', 'Апр', 'Май', 'Июн', 'Июл', 'Авг', 'Сен', 'Окт', 'Ноя', 'Дек']);
    expect(monthNames('en-US', 'short')[8]).toBe('Sep');
    const fr = monthNames('fr-FR', 'short');
    expect(new Set(fr).size).toBe(12); // «juin» и «juillet» на трёх буквах слились бы
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
      expect(firstDayOfWeek('en')).toBe(7); // без региона — регион языка по умолчанию (en → US)
      expect(firstDayOfWeek('en-AU')).toBe(1);
      expect(firstDayOfWeek('pt-PT')).toBe(7);
      expect(firstDayOfWeek('ar-EG')).toBe(6);
    } finally {
      if (saved) Object.defineProperty(proto, 'getWeekInfo', saved);
      if (savedInfo) Object.defineProperty(proto, 'weekInfo', savedInfo);
    }
  });

  // Запасная таблица — те же данные CLDR, что отдаёт getWeekInfo: сверка по регионам (ревью PR #1544).
  it('запасной путь совпадает с getWeekInfo по всем регионам', () => {
    const regions = ('AE AF AG AR AS AT AU BD BE BH BR BS BT BW BY BZ CA CH CN CO CZ DE DJ DK DM DO DZ EG ES '
      + 'ET FI FR GB GT GU HK HN ID IE IL IN IQ IR IS IT JM JO JP KE KH KR KW KZ LA LY MH MM MO MT MV MX MZ '
      + 'NI NL NO NP NZ OM PA PE PH PK PL PR PT PY QA RU SA SD SE SG SV SY TH TR TT TW UA UM US VE VI WS YE ZA ZW').split(' ');
    const native = Object.fromEntries(regions.map(r => [r, firstDayOfWeek(`und-${r}`)]));
    const proto = Intl.Locale.prototype as unknown as Record<string, unknown>;
    const saved = Object.getOwnPropertyDescriptor(proto, 'getWeekInfo');
    const savedInfo = Object.getOwnPropertyDescriptor(proto, 'weekInfo');
    delete proto.getWeekInfo;
    delete proto.weekInfo;
    try {
      expect(Object.fromEntries(regions.map(r => [r, firstDayOfWeek(`und-${r}`)]))).toEqual(native);
    } finally {
      if (saved) Object.defineProperty(proto, 'getWeekInfo', saved);
      if (savedInfo) Object.defineProperty(proto, 'weekInfo', savedInfo);
    }
  });
});
