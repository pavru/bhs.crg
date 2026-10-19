import { describe, it, expect } from 'vitest';
import { isoToRu, ruToISO, parseISO, toISO, daysInMonth, monthMatrix, dayInWeek } from './date';

describe('isoToRu', () => {
  it('converts full ISO to RU', () => {
    expect(isoToRu('2026-07-11')).toBe('11.07.2026');
  });
  it('passes through empty/other', () => {
    expect(isoToRu('')).toBe('');
    expect(isoToRu(null)).toBe('');
    expect(isoToRu('не-дата')).toBe('не-дата');
  });
});

describe('ruToISO', () => {
  it('converts RU to ISO', () => {
    expect(ruToISO('11.07.2026')).toBe('2026-07-11');
  });
  it('passes through incomplete', () => {
    expect(ruToISO('11.07')).toBe('11.07');
  });
});

describe('parseISO / toISO', () => {
  it('parses full ISO', () => {
    expect(parseISO('2026-07-11')).toEqual({ y: 2026, m: 7, d: 11 });
  });
  it('returns null on empty/garbage', () => {
    expect(parseISO('')).toBeNull();
    expect(parseISO(null)).toBeNull();
    expect(parseISO('2026-07')).toBeNull();
  });
  it('builds padded ISO', () => {
    expect(toISO(2026, 7, 1)).toBe('2026-07-01');
    expect(toISO(2026, 12, 31)).toBe('2026-12-31');
  });
});

describe('daysInMonth', () => {
  it('handles 30/31-day months', () => {
    expect(daysInMonth(2026, 1)).toBe(31);
    expect(daysInMonth(2026, 4)).toBe(30);
  });
  it('handles February leap/non-leap', () => {
    expect(daysInMonth(2024, 2)).toBe(29); // високосный
    expect(daysInMonth(2026, 2)).toBe(28);
  });
});

describe('monthMatrix (Monday-first)', () => {
  it('July 2026 starts on Wednesday (offset 2)', () => {
    // 1 июля 2026 — среда → две пустышки перед единицей (Пн, Вт).
    const w = monthMatrix(2026, 7);
    expect(w[0].slice(0, 3)).toEqual([null, null, 1]);
    expect(w[0][6]).toBe(5); // воскресенье первой недели
  });
  it('rows are full weeks of 7, covering all days', () => {
    const w = monthMatrix(2026, 2); // 28 дней, 1 фев 2026 — воскресенье
    expect(w.every(row => row.length === 7)).toBe(true);
    const days = w.flat().filter((x): x is number => x != null);
    expect(days).toEqual(Array.from({ length: 28 }, (_, i) => i + 1));
  });
});

describe('monthMatrix: неделя языка (issue #1461)', () => {
  it('с воскресенья: 1 октября 2026 (четверг) — пятая колонка', () => {
    expect(monthMatrix(2026, 10, 7)[0]).toEqual([null, null, null, null, 1, 2, 3]);
  });
  it('с понедельника по умолчанию — как раньше', () => {
    expect(monthMatrix(2026, 10)[0]).toEqual(monthMatrix(2026, 10, 1)[0]);
    expect(monthMatrix(2026, 10)[0]).toEqual([null, null, null, 1, 2, 3, 4]);
  });
});

describe('dayInWeek', () => {
  it('номер дня в неделе языка: 0 — её первый день', () => {
    const mon = new Date(2026, 9, 19), sun = new Date(2026, 9, 18);
    expect(dayInWeek(mon)).toBe(0);
    expect(dayInWeek(sun)).toBe(6);
    expect(dayInWeek(sun, 7)).toBe(0);
    expect(dayInWeek(mon, 7)).toBe(1);
    expect(dayInWeek(new Date(2026, 9, 17), 6)).toBe(0); // суббота у недели с субботы
  });
});
