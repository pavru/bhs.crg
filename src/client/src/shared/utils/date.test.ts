import { describe, it, expect } from 'vitest';
import { isoToRu, ruToISO, formatDateRu } from './date';

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
