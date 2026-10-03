import { describe, it, expect } from 'vitest';
import { gridColumnsOf } from './dataGridColumns';

describe('колонки сетки по сохранённым строкам (G1a, #1088)', () => {
  it('ключи ВСЕХ строк, а не первой: у union второй вариант не теряется', () => {
    const columns = gridColumnsOf([{ АОСР: 'a' }, { РеестрРабот: 'b' }],
      [{ key: 'АОСР', title: 'Акт' }, { key: 'РеестрРабот' }]);
    expect(columns.map(c => c.key)).toEqual(['АОСР', 'РеестрРабот']);
    expect(columns.map(c => c.label)).toEqual(['Акт', 'РеестрРабот']);
  });

  it('порядок — из схемы, а не из строки', () => {
    const columns = gridColumnsOf([{ б: 1, а: 2 }], [{ key: 'а' }, { key: 'б' }]);
    expect(columns.map(c => c.key)).toEqual(['а', 'б']);
  });

  it('ключ вне схемы — колонка с причиной removed', () => {
    const columns = gridColumnsOf([{ а: 1, старое: 2 }], [{ key: 'а' }]);
    expect(columns.find(c => c.key === 'старое')?.unavailable).toBe('removed');
    expect(columns.find(c => c.key === 'а')?.unavailable).toBeUndefined();
  });

  it('схемы нет — ключи без пометок', () => {
    const columns = gridColumnsOf([{ а: 1 }, { б: 2 }], null);
    expect(columns).toEqual([{ key: 'а', label: 'а' }, { key: 'б', label: 'б' }]);
  });
});
