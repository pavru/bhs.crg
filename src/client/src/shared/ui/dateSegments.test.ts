import { describe, expect, it } from 'vitest';
import { parsePasted, readSegments, segmentsOf, splitDate } from './dateSegments';

// Сегменты поля даты (issue #1460): что отдаётся наружу, что показывается, что разбирается во вставке.
describe('readSegments: наружу уходит только существующая дата', () => {
  it.each([
    ['31', '02', '2026'], ['29', '02', '2026'], ['00', '10', '2026'], ['39', '10', '2026'],
    ['18', '13', '2026'], ['18', '00', '2026'], ['18', '10', '0226'],
  ])('%s.%s.%s — такой даты нет', (d, m, y) => {
    expect(readSegments({ d, m, y }, 'day')).toEqual({ kind: 'impossible' });
  });

  it('29.02 високосного года и 31.12 — даты', () => {
    expect(readSegments({ d: '29', m: '02', y: '2028' }, 'day')).toEqual({ kind: 'date', iso: '2028-02-29' });
    expect(readSegments({ d: '31', m: '12', y: '2026' }, 'day')).toEqual({ kind: 'date', iso: '2026-12-31' });
  });

  it('недописанное — не дата и не пустота', () => {
    expect(readSegments({ d: '1', m: '10', y: '2026' }, 'day')).toEqual({ kind: 'partial' });
    expect(readSegments({ d: '18', m: '', y: '' }, 'day')).toEqual({ kind: 'partial' });
    expect(readSegments({ d: '18', m: '10', y: '26' }, 'day')).toEqual({ kind: 'partial' });
    expect(readSegments({ d: '', m: '', y: '' }, 'day')).toEqual({ kind: 'empty' });
  });

  it('точность: скрытые части — 01, месяц 13 у поля месяца — ошибка, день не смотрится', () => {
    expect(readSegments({ d: '', m: '10', y: '2026' }, 'month')).toEqual({ kind: 'date', iso: '2026-10-01' });
    expect(readSegments({ d: '99', m: '13', y: '2026' }, 'month')).toEqual({ kind: 'impossible' });
    expect(readSegments({ d: '', m: '', y: '2026' }, 'year')).toEqual({ kind: 'date', iso: '2026-01-01' });
    expect(readSegments({ d: '18', m: '10', y: '' }, 'year')).toEqual({ kind: 'empty' });
  });
});

describe('segmentsOf: значение не теряется на показе', () => {
  it('ISO со временем и ДД.ММ.ГГГГ из ячейки — сегментами', () => {
    expect(segmentsOf('2026-10-18T00:00:00')).toEqual({ d: '18', m: '10', y: '2026' });
    expect(segmentsOf('18.10.2026')).toEqual({ d: '18', m: '10', y: '2026' });
    expect(segmentsOf('')).toEqual({ d: '', m: '', y: '' });
  });

  it('не дата — null (показывается текстом), а не пустые сегменты', () => {
    expect(segmentsOf('вчера')).toBeNull();
    expect(segmentsOf('2026/10/18')).toBeNull();
  });
});

describe('parsePasted: дата целиком из вставки', () => {
  it.each([
    ['18.10.2026'], ['18/10/2026'], ['18-10-2026'], ['2026-10-18'], ['2026-10-18T09:30:00'], [' 18.10.26 '], ['8.1.2026'],
  ])('%s', text => {
    const got = parsePasted(text, 'day');
    expect(got && `${got.d}.${got.m}.${got.y}`).toBe(text.includes('8.1.') ? '08.01.2026' : '18.10.2026');
  });

  it('у поля месяца — и ММ.ГГГГ; у поля дня ММ.ГГГГ не дата', () => {
    expect(parsePasted('10.2026', 'month')).toEqual({ d: '', m: '10', y: '2026' });
    expect(parsePasted('10.2026', 'day')).toBeNull();
  });

  it('не дата — null: её разбирает сам сегмент', () => {
    expect(parsePasted('18', 'day')).toBeNull();
    expect(parsePasted('счёт от 18.10.2026', 'day')).toBeNull();
  });
});

// Один разбор на поле и на таблицу (ревью PR #1544): год — как набран, решает вызывающий.
describe('splitDate', () => {
  it('порядок языка, точность и год как набран', () => {
    expect(splitDate('10/18/26', 'day', ['m', 'd', 'y'])).toEqual({ d: '18', m: '10', y: '26' });
    expect(splitDate('2026/10/18', 'day', ['y', 'm', 'd'])).toEqual({ d: '18', m: '10', y: '2026' });
    expect(splitDate('2026', 'year', ['d', 'm', 'y'])).toEqual({ d: '', m: '', y: '2026' });
    expect(splitDate('2026', 'month', ['d', 'm', 'y'])).toBeNull();
    expect(splitDate('2026-10-18T10:00', 'day', ['m', 'd', 'y'])).toEqual({ d: '18', m: '10', y: '2026' });
    expect(splitDate('18.10.202', 'day', ['d', 'm', 'y'])).toBeNull();
  });
});
