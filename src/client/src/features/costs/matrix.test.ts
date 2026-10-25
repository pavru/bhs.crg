import { describe, expect, it } from 'vitest';
import type { AllocationPartView, InvoiceLineView, InvoiceView, LineAllocationView } from '@/shared/api/invoices';
import type { Place } from './places';
import {
  DOCUMENT_ROW, cellText, cellsFromState, cellsOf, headerObject, headerPickMode, matrixSignature, restText, rowsOf, targetsOf, toState,
  type MatrixRow,
} from './matrix';

function part(construction: string, overrides: Partial<AllocationPartView> = {}): AllocationPartView {
  return {
    id: `${construction}-part`, ordinal: 1, constructionId: construction, constructionName: construction,
    sectionId: null, sectionName: null, articleId: null, articleName: null, targetLost: false, quantity: 3, amount: 145, rounding: 0,
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
      stamp: '0',
    },
  } as unknown as InvoiceView;
}

const site = (construction: string) => ({ construction, section: null, article: null });
const article = (id: string) => ({ construction: null, section: null, article: id });

/** Часть на статью вне строек (F3): стройки у неё нет. */
function articlePart(id: string, overrides: Partial<AllocationPartView> = {}): AllocationPartView {
  return part('', { constructionId: null, constructionName: null, articleId: id, articleName: id, ...overrides });
}

const quantityRow: MatrixRow ={ key: 'l1', lineId: 'l1', title: '1. Кабель', mode: 'quantity', unit: 'шт', whole: 10, amount: 483.3 };

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
    const [a, b] = [{ key: 'a', ...site('A'), percent: '' }, { key: 'b', ...site('B'), percent: '' }];

    const state = toState(rows, [a, b], { l1: { a: '4', b: '0' }, l2: { a: 'abc' } });

    expect(state.lines).toEqual([
      { line: 'l1', parts: [{ ...site('A'), quantity: 4, amount: null }] },
      { line: 'l2', parts: [{ ...site('A'), quantity: null, amount: null }] },
    ]);
    expect(state.document).toEqual([]);
  });

  it('предпросмотр раскладывается по колонкам своих целей', () => {
    const rows = rowsOf(invoice([line('l1', {})]), null);
    const cells = cellsFromState(rows, [{ key: 'x', ...site('A'), percent: '' }], {
      lines: [{ line: 'l1', parts: [{ ...site('A'), quantity: 3, amount: null }] }], document: [],
    });
    expect(cells.l1.x).toBe('3');
  });
});

describe('объект в шапке', () => {
  /** Один объект; `sections` — занятые разделы, `bare` — есть ли часть без раздела. */
  const one = (place: Place, section: string | null | 'many', sections = section === null ? 0 : 1,
    bare = section === null, complete = false) => ({ kind: 'one', place, section, sections, bare, complete });

  it('выведен из разноски: нет, один (полностью или нет), несколько', () => {
    expect(headerObject(invoice([line('l1', {})])).kind).toBe('none');
    expect(headerObject(invoice([line('l1', { parts: [part('A')] })]))).toEqual(one(site('A'), null));
    expect(headerObject(invoice([line('l1', { parts: [part('A'), part('B')] })]))).toEqual({ kind: 'many', count: 2 });
    // Разделы одной стройки — всё ещё один объект, и счёт объектов — по стройкам.
    const sections = invoice([line('l1', { parts: [part('A', { sectionId: '1' }), part('A', { sectionId: '2' }), part('B')] })]);
    expect(headerObject(sections)).toEqual({ kind: 'many', count: 2 });
  });

  it('весь счёт на раздел — стройка и раздел; по строкам и частью счёта целиком', () => {
    const view = invoice([
      line('l1', { parts: [part('A', { sectionId: '4' })] }),
      line('l2', { parts: [part('A', { sectionId: '4' })] }),
    ]);
    expect(headerObject(view)).toEqual(one(site('A'), '4'));
    expect(headerObject(invoice([], [part('A', { sectionId: '4' })]))).toEqual(one(site('A'), '4'));
  });

  it('несколько разделов одной стройки — один объект, раздел «many» и их число', () => {
    const view = invoice([
      line('l1', { parts: [part('A', { sectionId: '4' }), part('A', { sectionId: '5' })] }),
      line('l2', { parts: [part('A', { sectionId: '6' })] }),
    ]);
    expect(headerObject(view)).toEqual(one(site('A'), 'many', 3));
  });

  it('раздел вместе с «без раздела» той же стройки — тоже несколько мест', () => {
    const view = invoice([line('l1', { parts: [part('A', { sectionId: '4' }), part('A')] })]);
    // Один раздел и стройка без раздела — «1 раздел и без раздела», а не «2 раздела».
    expect(headerObject(view)).toEqual(one(site('A'), 'many', 1, true));
  });

  it('удалённый раздел — своим id: подпись решает placeName', () => {
    const view = invoice([line('l1', { parts: [part('A', { sectionId: 'удалённый', targetIssue: 'section-lost' })] })]);
    expect(headerObject(view)).toEqual(one(site('A'), 'удалённый'));
  });

  it('статья — без раздела', () => {
    expect(headerObject(invoice([line('l1', { parts: [articlePart('склад')] })]))).toEqual(one(article('склад'), null));
  });
});

// Таблица D3: какой выбор в шапке пишется сразу, а какой открывает матрицу.
describe('выбор в шапке: сразу или матрица', () => {
  const at = (sections: AllocationPartView[], complete = true) => {
    const view = invoice([line('l1', { parts: sections })]);
    view.allocation.allocated = complete;
    return headerObject(view);
  };
  const section = (construction: string, id: string | null) => ({ construction, section: id, article: null });

  it('не разнесён — любое место сразу', () => {
    const none = headerObject(invoice([line('l1', {})]));
    expect(headerPickMode(none, section('A', '4'))).toBe('write');
    expect(headerPickMode(none, article('склад'))).toBe('write');
  });

  it('весь счёт на стройке без раздела: раздел той же стройки — сразу', () => {
    expect(headerPickMode(at([part('A')]), section('A', '4'))).toBe('write');
  });

  it('весь счёт на разделе: другой раздел или «вся стройка» — сразу', () => {
    expect(headerPickMode(at([part('A', { sectionId: '4' })]), section('A', '5'))).toBe('write');
    expect(headerPickMode(at([part('A', { sectionId: '4' })]), section('A', null))).toBe('write');
  });

  it('то же место у полностью разнесённого счёта — писать нечего', () => {
    expect(headerPickMode(at([part('A', { sectionId: '4' })]), section('A', '4'))).toBe('keep');
    expect(headerPickMode(at([part('A')]), section('A', null))).toBe('keep');
    expect(headerPickMode(at([articlePart('склад')]), article('склад'))).toBe('keep');
  });

  it('неполная разноска на одном месте стройки — тоже сразу: остаток ложится туда же', () => {
    expect(headerPickMode(at([part('A')], false), section('A', '4'))).toBe('write');
    expect(headerPickMode(at([part('A', { sectionId: '4' })], false), section('A', '4'))).toBe('write');
  });

  it('несколько разделов стройки — матрица, даже на один из них или на всю стройку', () => {
    const many = at([part('A', { sectionId: '4' }), part('A', { sectionId: '5' })]);
    expect(headerPickMode(many, section('A', '4'))).toBe('matrix');
    expect(headerPickMode(many, section('A', null))).toBe('matrix');
  });

  it('другая стройка или статья — матрица', () => {
    expect(headerPickMode(at([part('A', { sectionId: '4' })]), section('B', null))).toBe('matrix');
    expect(headerPickMode(at([part('A')]), article('склад'))).toBe('matrix');
  });

  it('статья: другое место — матрица, та же статья — сразу', () => {
    expect(headerPickMode(at([articlePart('склад')]), section('A', null))).toBe('matrix');
    expect(headerPickMode(at([articlePart('склад')]), article('офис'))).toBe('matrix');
    expect(headerPickMode(at([articlePart('склад')], false), article('склад'))).toBe('write');
  });

  it('несколько объектов — матрица', () => {
    expect(headerPickMode(at([part('A'), part('B')]), section('A', null))).toBe('matrix');
  });
});

describe('статья вне строек (F3)', () => {
  it('статья — своя колонка, и её часть не путается со стройкой', () => {
    const view = invoice([line('l1', { parts: [part('A'), articlePart('склад')] })]);
    const targets = targetsOf(view);
    expect(targets.map(({ construction, section, article: a }) => ({ construction, section, article: a })))
      .toEqual([site('A'), article('склад')]);

    const rows = rowsOf(view, null);
    const cells = cellsOf(rows, targets, { l1: view.lines[0].allocation });
    expect(Object.keys(cells.l1)).toHaveLength(2);
  });

  it('в набор записи статья уезжает статьёй, без стройки', () => {
    const rows = rowsOf(invoice([line('l1', {})]), null);
    const state = toState(rows, [{ key: 's', ...article('склад'), percent: '' }], { l1: { s: '2' } });
    expect(state.lines[0].parts).toEqual([{ construction: null, section: null, article: 'склад', quantity: 2, amount: null }]);
  });

  it('счёт на склад — в шапке один объект, и это статья', () => {
    expect(headerObject(invoice([line('l1', { parts: [articlePart('склад')] })])))
      .toMatchObject({ kind: 'one', place: article('склад'), complete: false });
    expect(headerObject(invoice([line('l1', { parts: [part('A'), articlePart('склад')] })])))
      .toEqual({ kind: 'many', count: 2 });
  });
});

// Подпись — то, по чему матрица узнаёт «строки или разноску изменили» (issue #1176).
describe('подпись матрицы', () => {
  // Название позиции приезжает в виде счёта, а меняется в справочнике — без счёта и без его версии.
  // Попади оно в подпись, переименованная позиция читалась бы как чужая правка строк (ревью PR #1208).
  it('не зависит от названия позиции номенклатуры', () => {
    const before = invoice([line('l1', {})]);
    const after = invoice([{ ...line('l1', {}), nomenclatureName: 'переименовали', nomenclatureLost: true }]);
    expect(matrixSignature(after)).toBe(matrixSignature(before));
  });

  it('меняется с количеством строки и с отметкой разноски', () => {
    const before = invoice([line('l1', {})]);
    expect(matrixSignature(invoice([line('l1', {}, { quantity: 99 })]))).not.toBe(matrixSignature(before));
    expect(matrixSignature({ ...before, allocation: { ...before.allocation, stamp: 'другая' } }))
      .not.toBe(matrixSignature(before));
  });
});
