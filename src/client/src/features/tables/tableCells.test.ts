import { describe, it, expect } from 'vitest';
import { tableFilterColumns, type TableColumn, type TableData } from '@/shared/api/tables';
import { cellText, gridColumns, gridState, shownOf } from './tableCells';

/** Таблица модуля → сетка и отбор (задача G1d, issue #1091). */

const column = (patch: Partial<TableColumn>): TableColumn => ({
  key: 'Номер', label: 'Номер счёта', kind: 'text', operators: ['eq'], system: true,
  unavailable: null, reason: null, dependsOnFilter: false, note: null, options: null, ...patch,
});

const table = (patch: Partial<TableData>): TableData => ({
  address: 'costs.invoices', title: 'Счета на оплату', grain: 'счёт', boundary: '', columns: [], rows: [],
  state: null, count: 0, offset: 0, limit: 200, ...patch,
});

describe('gridColumns', () => {
  it('число — вправо; подпись смысла под отбором — в заголовке', () => {
    const [amount] = gridColumns([column({ key: 'СуммаПоОтбору', label: 'Сумма', kind: 'number', note: 'доля: Комарова 36' })]);
    expect(amount).toMatchObject({ label: 'Сумма (доля: Комарова 36)', align: 'right' });
  });

  it('причина недоступности колонки доезжает кодом; незнакомый код колонку не роняет', () => {
    expect(gridColumns([column({ unavailable: 'no-right', reason: 'нет права на суммы' })])[0].unavailable).toBe('no-right');
    expect(gridColumns([column({ unavailable: 'module-off' })])[0].unavailable).toBeNull();
  });
});

describe('cellText', () => {
  it('дата — днём, флаг — словом, перечень — через запятую', () => {
    expect(cellText('2026-10-03', 'date')).toBe('03.10.2026');
    expect(cellText(false, 'boolean')).toBe('нет');
    expect(cellText(['Комарова 36', 'Склад'], 'list')).toBe('Комарова 36, Склад');
    expect(cellText('СЧ-1', 'text')).toBe('СЧ-1');
  });
});

describe('gridState — пустая выдача под отбором и без него отвечает разное', () => {
  it('под отбором — «отбор ничего не нашёл», без отбора — «строк нет»', () => {
    expect(gridState(table({}), true)).toBe('filtered-out');
    expect(gridState(table({}), false)).toBe('no-data');
  });

  it('выключенный модуль перекрывает оба', () => {
    expect(gridState(table({ state: 'module-off' }), true)).toBe('module-off');
  });
});

describe('shownOf', () => {
  it('страница меньше отбора — названа числом, а не обрезана молча', () => {
    expect(shownOf(table({ rows: [{}, {}], count: 1340 }))).toMatch(/^Показано 2 из 1.340$/);
    expect(shownOf(table({ rows: [{}, {}], count: 2 }))).toBe('Строк: 2');
  });
});

describe('tableFilterColumns', () => {
  it('условие встанет на ключ, человеку — заголовок; перечень и операторы — как прислал сервер', () => {
    const [state] = tableFilterColumns([column({
      key: 'СостояниеОплаты', label: 'Состояние оплаты', kind: 'choice', operators: ['eq', 'in'], options: ['Оплачен'],
    })]);
    expect(state).toEqual({
      name: 'СостояниеОплаты', label: 'Состояние оплаты', kind: 'choice', operators: ['eq', 'in'],
      options: ['Оплачен'], unavailable: undefined,
    });
  });

  it('колонка, чьё значение зависит от отбора, в отбор не идёт; закрытая остаётся с причиной', () => {
    const columns = tableFilterColumns([
      column({ key: 'СуммаПоОтбору', dependsOnFilter: true, operators: [] }),
      column({ key: 'Итого', unavailable: 'no-right', reason: 'нет права на суммы' }),
    ]);
    expect(columns.map(c => c.name)).toEqual(['Итого']);
    expect(columns[0].unavailable).toBe('нет права на суммы');
  });
});
