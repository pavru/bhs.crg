import { describe, it, expect } from 'vitest';
import { isoToRu, ruToISO, formatDateRu, parseISO, toISO, daysInMonth, monthMatrix } from './date';

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

describe('formatDateRu (issue #60)', () => {
  const iso = '2026-07-11';
  it('day precision (default) shows full date', () => {
    expect(formatDateRu(iso)).toBe('11.07.2026');
    expect(formatDateRu(iso, 'day')).toBe('11.07.2026');
  });
  it('month precision hides the day', () => {
    expect(formatDateRu(iso, 'month')).toBe('07.2026');
  });
  it('year precision shows only the year', () => {
    expect(formatDateRu(iso, 'year')).toBe('2026');
  });
  it('formats padded partial dates by precision (values stored full ISO)', () => {
    // year-точность хранит YYYY-01-01, month — YYYY-MM-01; показываем только значимую часть
    expect(formatDateRu('2026-01-01', 'year')).toBe('2026');
    expect(formatDateRu('2026-07-01', 'month')).toBe('07.2026');
  });
  it('passes through empty/unparseable', () => {
    expect(formatDateRu('')).toBe('');
    expect(formatDateRu(null, 'year')).toBe('');
    expect(formatDateRu('мусор', 'month')).toBe('мусор');
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
