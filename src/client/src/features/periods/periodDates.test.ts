import { describe, expect, it } from 'vitest';
import { ruDate, suggestFirstFrom, suggestThrough } from './periodDates';

describe('даты закрытия периода', () => {
  it('показывает день по-русски, не сдвигая его поясом', () => {
    expect(ruDate('2026-09-30')).toBe('30.09.2026');
    expect(ruDate('2026-01-01')).toBe('01.01.2026');
  });

  it('предлагает конец прошлого месяца', () => {
    expect(suggestThrough('2026-10-04', null)).toBe('2026-09-30');
    expect(suggestThrough('2026-10-04', '2026-08-31')).toBe('2026-09-30');
    // Январь: прошлый месяц — декабрь прошлого года; март — февраль, в том числе високосный.
    expect(suggestThrough('2026-01-15', null)).toBe('2025-12-31');
    expect(suggestThrough('2028-03-10', null)).toBe('2028-02-29');
  });

  it('когда прошлый месяц закрыт, предлагает вчера, а когда закрыто и вчера — ничего', () => {
    expect(suggestThrough('2026-10-04', '2026-09-30')).toBe('2026-10-03');
    expect(suggestThrough('2026-10-04', '2026-10-03')).toBe('');
    // Первое число: вчера — это и есть конец прошлого месяца.
    expect(suggestThrough('2026-10-01', '2026-09-30')).toBe('');
  });

  it('начало первого закрытия — первый день того же месяца', () => {
    expect(suggestFirstFrom('2026-09-30')).toBe('2026-09-01');
    expect(suggestFirstFrom('2026-10-03')).toBe('2026-10-01');
    expect(suggestFirstFrom('')).toBe('');
  });
});
