import { describe, it, expect } from 'vitest';
import type { TableTotal } from '@/shared/api/tables';
import { aggregatesFor, hasShownTotals, totalText } from './tableTotals';

/** Итоговая строка таблицы (задача G1e, issue #1092, ТЗ CORE-33). */

const total = (patch: Partial<TableTotal>): TableTotal => ({
  count: 3, skipped: 0, skippedReason: null, sum: 1500.5, average: 500.17, min: 100, max: 900.5, ...patch,
});

describe('aggregatesFor — итог зависит от вида колонки', () => {
  it('у числа все итоги, у даты — края и количество, у остального — количество', () => {
    expect(aggregatesFor('number')).toEqual(['sum', 'avg', 'min', 'max', 'count']);
    expect(aggregatesFor('date')).toEqual(['min', 'max', 'count']);
    expect(aggregatesFor('text')).toEqual(['count']);
    expect(aggregatesFor('choice')).toEqual(['count']);
  });
});

describe('totalText', () => {
  it('каждый итог показан своим знаком и своим числом', () => {
    expect(totalText(total({}), 'sum', 'number')?.text).toMatch(/^Σ 1.500,5$/);
    expect(totalText(total({}), 'avg', 'number')?.text).toBe('ср. 500,17');
    expect(totalText(total({}), 'min', 'number')?.text).toBe('мин. 100');
    expect(totalText(total({}), 'max', 'number')?.text).toBe('макс. 900,5');
    expect(totalText(total({}), 'count', 'number')?.text).toBe('кол-во 3');
  });

  it('у даты края показаны днём, а количество — числом', () => {
    const dates = total({ sum: null, average: null, min: '2026-09-03', max: '2026-10-01' });
    expect(totalText(dates, 'min', 'date')?.text).toBe('мин. 03.09.2026');
    expect(totalText(dates, 'max', 'date')?.text).toBe('макс. 01.10.2026');
    expect(totalText(dates, 'count', 'date')?.text).toBe('кол-во 3');
  });

  it('неучтённые значения названы числом и причиной — сумма не становится молча меньше', () => {
    expect(totalText(total({ skipped: 3, skippedReason: 'не число' }), 'sum', 'number')?.note)
      .toBe('не учтено 3 значения: не число');
    expect(totalText(total({ skipped: 1, skippedReason: 'не дата' }), 'min', 'date')?.note)
      .toBe('не учтено 1 значение: не дата');
    expect(totalText(total({ skipped: 12, skippedReason: 'не число' }), 'avg', 'number')?.note)
      .toBe('не учтено 12 значений: не число');
    expect(totalText(total({}), 'sum', 'number')?.note).toBeNull();
  });

  it('значений нет — прочерк, а не ноль: сумма пустого и сумма, равная нулю, — разные ответы', () => {
    expect(totalText(total({ count: 0, sum: null }), 'sum', 'number')?.text).toBe('Σ —');
    expect(totalText(total({ sum: 0 }), 'sum', 'number')?.text).toBe('Σ 0');
  });

  it('итог, которого у вида колонки не бывает, так и назван — а не посчитан как попало', () => {
    const sumOfText = totalText(total({ sum: null }), 'sum', 'text');
    expect(sumOfText).toEqual({ text: 'Σ —', note: 'у колонки этого вида такой итог не считается', meaning: null });
  });

  it('сервер итога не прислал — показать нечего', () => {
    expect(totalText(undefined, 'sum', 'number')).toBeNull();
  });

  it('что итог значит под отбором — словами сервера; оговорки нет — и подписи нет', () => {
    const axis = 'период — по дате счёта, не по оплате';
    expect(totalText(total({ note: axis }), 'sum', 'number')?.meaning).toBe(axis);
    expect(totalText(total({}), 'sum', 'number')?.meaning).toBeNull();
  });
});

describe('hasShownTotals — рисовать ли итоговую строку', () => {
  const chosen = [{ column: 'Сумма', aggregate: 'sum' as const }, { column: 'Итого', aggregate: 'sum' as const }];
  const grid = [{ key: 'Поставщик' }, { key: 'Итого' }];

  it('итог посчитан и его колонка в сетке — строка есть', () => {
    expect(hasShownTotals(chosen, { Итого: total({}) }, grid)).toBe(true);
  });

  it('обе колонки с итогом закрыты правом — строки нет, а не пустая полоса', () => {
    expect(hasShownTotals(chosen, {}, [{ key: 'Поставщик' }])).toBe(false);
    expect(hasShownTotals(chosen, null, grid)).toBe(false);
  });

  it('итог пришёл, а колонку из сетки убрали — показать его негде', () => {
    expect(hasShownTotals(chosen, { Сумма: total({}) }, grid)).toBe(false);
  });

  it('итогов не выбрано — строки нет', () => {
    expect(hasShownTotals([], { Итого: total({}) }, grid)).toBe(false);
  });
});
