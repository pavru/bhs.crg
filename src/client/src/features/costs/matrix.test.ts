import { describe, expect, it } from 'vitest';
import type { AllocationPartView, InvoiceLineView, InvoiceView, LineAllocationView } from '@/shared/api/invoices';
import {
  DOCUMENT_ROW, cellText, cellsFromState, cellsOf, headerObject, restText, rowsOf, targetsOf, toState,
  type MatrixRow,
} from './matrix';

function part(construction: string, overrides: Partial<AllocationPartView> = {}): AllocationPartView {
  return {
    id: `${construction}-part`, ordinal: 1, constructionId: construction, constructionName: construction,
    sectionId: null, sectionName: null, targetLost: false, quantity: 3, amount: 145, rounding: 0,
    discrepancy: 0, mismatched: false, ...overrides,
  };
}

function line(id: string, allocation: Partial<LineAllocationView>, overrides: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id, ordinal: 1, nomenclatureId: null, nomenclatureName: 'Кабель', nomenclatureLost: false, supplierText: null,
    supplierCode: null, unit: 'шт', quantity: 10, price: 48.33, vatRate: null, vatAmount: null, amount: 483.3,
    allocation: { mode: 'quantity', parts: [], unallocatedQuantity: 10, unallocatedAmount: 483.3, balanced: false, ...allocation },
    ...overrides,
  } as InvoiceLineView;
}

function invoice(lines: InvoiceLineView[], documentParts: AllocationPartView[] = []): InvoiceView {
  return {
    id: 'i1', lines, requisites: {},
    allocation: {
      allocated: false, unbalanced: [], lost: 0, discrepancy: null, tolerance: 1, withinTolerance: true,
      document: { parts: documentParts, unallocatedAmount: null, pending: documentParts.length > 0 && lines.length > 0, balanced: false },
    },
  } as unknown as InvoiceView;
}

const quantityRow: MatrixRow = { key: 'l1', lineId: 'l1', title: '1. Кабель', mode: 'quantity', unit: 'шт', whole: 10, amount: 483.3 };

describe('колонки матрицы', () => {
  it('объекты по первому появлению — строки, потом разноска суммой, без повторов', () => {
    const view = invoice([
      line('l1', { parts: [part('B'), part('A')] }),
      line('l2', { parts: [part('A'), part('C')] }),
    ]);
    expect(targetsOf(view).map(t => t.construction)).toEqual(['B', 'A', 'C']);
  });

  it('у счёта без строк — одна строка «счёт целиком» суммой к оплате', () => {
    const [row] = rowsOf(invoice([]), 1000);
    expect(row).toMatchObject({ key: DOCUMENT_ROW, lineId: null, mode: 'amount', whole: 1000 });
  });
});

describe('клетки', () => {
  it('ноль пишется, а не остаётся пустым', () => {
    expect(cellText(quantityRow, undefined)).toBe('0');
    expect(restText(quantityRow, { mode: 'quantity', parts: [], unallocatedQuantity: 0, unallocatedAmount: 0, balanced: true }))
      .toBe('0 шт');
  });

  it('клетка строки с количеством — количество и посчитанная сервером сумма', () => {
    expect(cellText(quantityRow, part('A', { quantity: 4, amount: 193.32 })).replace(/\s/g, ' '))
      .toBe('4 шт · 193,32 ₽');
  });

  it('черновик суммы — без расхождения со счётом: его добавил сервер при чтении', () => {
    const view = invoice([line('l1', { mode: 'amount', parts: [part('A', { quantity: null, amount: 100.5, discrepancy: 0.5 })] },
      { quantity: null, amount: 100 })]);
    const rows = rowsOf(view, null);
    const targets = targetsOf(view);
    expect(cellsOf(rows, targets, { l1: view.lines[0].allocation }).l1[targets[0].key]).toBe('100');
  });
});

describe('набор для записи', () => {
  it('каждая строка — в наборе, пустые и нулевые клетки частей не дают, набранное не-число уезжает пустым', () => {
    const view = invoice([line('l1', {}), line('l2', {}, { quantity: null, amount: 50 })]);
    const rows = rowsOf(view, null);
    const [a, b] = [{ key: 'a', construction: 'A', section: null, percent: '' }, { key: 'b', construction: 'B', section: null, percent: '' }];

    const state = toState(rows, [a, b], { l1: { a: '4', b: '0' }, l2: { a: 'abc' } });

    expect(state.lines).toEqual([
      { line: 'l1', parts: [{ construction: 'A', section: null, quantity: 4, amount: null }] },
      { line: 'l2', parts: [{ construction: 'A', section: null, quantity: null, amount: null }] },
    ]);
    expect(state.document).toEqual([]);
  });

  it('предпросмотр раскладывается по колонкам своих целей', () => {
    const rows = rowsOf(invoice([line('l1', {})]), null);
    const cells = cellsFromState(rows, [{ key: 'x', construction: 'A', section: null, percent: '' }], {
      lines: [{ line: 'l1', parts: [{ construction: 'A', section: null, quantity: 3, amount: null }] }], document: [],
    });
    expect(cells.l1.x).toBe('3');
  });
});

describe('объект в шапке', () => {
  it('выведен из разноски: нет, один (полностью или нет), несколько', () => {
    expect(headerObject(invoice([line('l1', {})])).kind).toBe('none');
    expect(headerObject(invoice([line('l1', { parts: [part('A')] })]))).toEqual({ kind: 'one', construction: 'A', complete: false });
    expect(headerObject(invoice([line('l1', { parts: [part('A'), part('B')] })]))).toEqual({ kind: 'many', count: 2 });
  });
});
