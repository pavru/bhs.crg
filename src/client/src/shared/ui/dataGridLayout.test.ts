import { describe, expect, it } from 'vitest';
import { FLEX_COLUMN_MIN, fixedTableMinWidth } from './dataGridLayout';

describe('fixedTableMinWidth: гибкая колонка не сжимается в ноль', () => {
  it('сумма шагов и минимум гибкой колонки — строки редакции сметы', () => {
    // № 12, Шифр 36, Объём 20, Ед. 16, Вид работы 40, Стоимость 32 и наименование без ширины.
    const columns = [
      { width: 'w-12' }, { width: 'w-36' }, {}, { width: 'w-20' }, { width: 'w-16' }, { width: 'w-40' }, { width: 'w-32' },
    ];
    expect(fixedTableMinWidth(columns)).toBe(`calc(var(--spacing) * ${156 + FLEX_COLUMN_MIN})`);
  });

  it('минимум — каждой гибкой колонке', () => {
    expect(fixedTableMinWidth([{}, { width: 'w-10' }, {}])).toBe(`calc(var(--spacing) * ${10 + 2 * FLEX_COLUMN_MIN})`);
  });

  it('гибких колонок нет — сжиматься нечему, наименьшей ширины нет', () => {
    expect(fixedTableMinWidth([{ width: 'w-12' }, { width: 'w-36' }])).toBeUndefined();
  });

  it('доля ширины берётся от самой таблицы', () => {
    // w-10 и гибкая колонка делят оставшиеся 3/5 таблицы.
    expect(fixedTableMinWidth([{ width: 'w-10' }, { width: 'w-2/5' }, {}]))
      .toBe(`calc(var(--spacing) * ${10 + FLEX_COLUMN_MIN} / 0.6)`);
  });

  it('ширина рядом с другими классами и непрочитанная ширина', () => {
    // Класс ширины ищется среди прочих; w-auto и w-[…] не прочесть — колонка считается гибкой.
    expect(fixedTableMinWidth([{ width: 'text-right w-24' }, { width: 'w-auto' }, { width: 'w-[13rem]' }]))
      .toBe(`calc(var(--spacing) * ${24 + 2 * FLEX_COLUMN_MIN})`);
  });
});
