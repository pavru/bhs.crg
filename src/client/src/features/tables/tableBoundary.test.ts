import { describe, expect, it } from 'vitest';
import { tableBoundary, tableSubtitle } from './tableBoundary';

describe('tableBoundary', () => {
  it('берёт границу применившегося исхода из ответа строк, а не из описания', () => {
    expect(tableBoundary({ boundary: null }, { boundary: 'Отдаёт то, что видно вам: отчёты ваших строек (4)' }))
      .toBe('Отдаёт то, что видно вам: отчёты ваших строек (4)');
    expect(tableBoundary({ boundary: 'Отдаёт всё' }, { boundary: 'Отдаёт всё, но иначе' })).toBe('Отдаёт всё, но иначе');
  });

  it('пока строк нет — граница описания; у таблицы с несколькими исходами её нет вовсе', () => {
    expect(tableBoundary({ boundary: 'Отдаёт всё' }, undefined)).toBe('Отдаёт всё');
    expect(tableBoundary({ boundary: null }, null)).toBeNull();
  });
});

describe('tableSubtitle', () => {
  it('без границы не ставит пустого предложения', () => {
    expect(tableSubtitle('отчёт', null)).toBe('Строка — отчёт.');
    expect(tableSubtitle('счёт', 'Отдаёт все счета.')).toBe('Строка — счёт. Отдаёт все счета.');
  });
});
