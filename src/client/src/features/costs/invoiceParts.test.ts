import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { K } from './invoiceFields';
import { PART_ORDER, initiallyOpen, partFilled, partLine, partOfField } from './invoiceParts';

const ref = (id: string) => ({ $ref: 'catalog', entryId: id });

/** Черновик с заполненной первой частью, одной строкой с позицией, разнесённый, со сроком. */
const view = (over: Partial<InvoiceView> & { requisites?: Record<string, unknown> } = {}): InvoiceView => ({
  id: 'i1',
  requisites: {
    [K.supplier]: ref('s'), [K.payer]: ref('p'), [K.number]: 'СЧ-104', [K.date]: '2026-09-03',
    [K.total]: 128400.5, [K.vat]: 21400.08, [K.dueDate]: '2026-10-03', ...over.requisites,
  },
  unconfirmed: [],
  lines: [{ id: 'l1', nomenclatureId: 'n1' }],
  allocation: { allocated: true },
  payment: { paid: false, paidOn: null },
  readiness: { required: [], gaps: [] },
  ...over,
  ...(over.requisites ? { requisites: { ...defaults(), ...over.requisites } } : {}),
} as unknown as InvoiceView);

function defaults() {
  return {
    [K.supplier]: ref('s'), [K.payer]: ref('p'), [K.number]: 'СЧ-104', [K.date]: '2026-09-03',
    [K.total]: 128400.5, [K.vat]: 21400.08, [K.dueDate]: '2026-10-03',
  };
}

const clean = { edits: {}, linesDirty: false };

describe('partFilled — «Кто и сколько»', () => {
  it('пять полей заданы, меток нет — заполнена', () => {
    expect(partFilled('who', view(), clean)).toBe(true);
  });

  it('метка «со скана, не проверено» держит часть раскрытой, как бы полно она ни была заполнена', () => {
    expect(partFilled('who', view({ unconfirmed: [K.total] }), clean)).toBe(false);
  });

  it('правка поля снимает метку — и часть снова заполнена', () => {
    expect(partFilled('who', view({ unconfirmed: [K.total] }), { edits: { [K.total]: '128400,50' }, linesDirty: false })).toBe(true);
  });

  it('ошибка значения — пункт готовности о поле части — держит часть раскрытой', () => {
    const gaps = [{ kind: 'field', label: 'сумма расходится', field: K.total }];
    expect(partFilled('who', view({ readiness: { required: [], gaps } }), clean)).toBe(false);
  });

  it('удалённая запись справочника у стороны — не «заполнено»', () => {
    expect(partFilled('who', view({ references: { supplier: 'lost', payer: 'present', documentType: 'present' } } as Partial<InvoiceView>), clean)).toBe(false);
  });

  it('без плательщика и при сумме ноль — не заполнена', () => {
    expect(partFilled('who', view({ requisites: { [K.payer]: null } }), clean)).toBe(false);
    expect(partFilled('who', view({ requisites: { [K.total]: 0 } }), clean)).toBe(false);
  });
});

describe('partFilled — остальные части', () => {
  it('строки: есть, у каждой позиция, несохранённых нет', () => {
    expect(partFilled('lines', view(), clean)).toBe(true);
    expect(partFilled('lines', view({ lines: [] }), clean)).toBe(false);
    expect(partFilled('lines', view({ lines: [{ id: 'l1', nomenclatureId: null }] } as unknown as Partial<InvoiceView>), clean)).toBe(false);
  });

  it('несохранённые строки — не «заполнены»', () => {
    expect(partFilled('lines', view(), { edits: {}, linesDirty: true })).toBe(false);
  });

  it('разноска — когда сходится; сроки — когда срок определён', () => {
    expect(partFilled('allocation', view({ allocation: { allocated: false } } as unknown as Partial<InvoiceView>), clean)).toBe(false);
    expect(partFilled('terms', view({ requisites: { [K.dueDate]: null } }), clean)).toBe(false);
    expect(partFilled('terms', view(), clean)).toBe(true);
  });

  it('«Ещё» и «История» открываются свёрнутыми', () => {
    const open = initiallyOpen(view({ lines: [], allocation: { allocated: false } } as unknown as Partial<InvoiceView>), clean);
    expect(open.more).toBe(false);
    expect(open.history).toBe(false);
    expect(open.lines).toBe(true);
  });

  it('«Ещё» с меткой со скана раскрыта: метку прятать нельзя', () => {
    expect(initiallyOpen(view({ unconfirmed: [K.basis] }), clean).more).toBe(true);
  });
});

describe('части', () => {
  it('порядок — порядок работы', () => {
    expect(PART_ORDER).toEqual(['who', 'lines', 'allocation', 'terms', 'more', 'history']);
  });

  it('плательщик — в «Кто и сколько», срок — в «Сроки и оплата»', () => {
    expect(partOfField(K.payer)).toBe('who');
    expect(partOfField(K.dueDate)).toBe('terms');
    expect(partOfField(K.basis)).toBe('more');
  });

  it('строка-итог первой части: «Поставщик → плательщик · № … от … · к оплате …»', () => {
    const names: Record<string, string> = { [K.supplier]: 'ООО «Кабель-Торг»', [K.payer]: 'ООО «СЭМ»' };
    expect(partLine('who', view(), clean, key => names[key] ?? null))
      .toMatch(/^ООО «Кабель-Торг» → ООО «СЭМ» · № СЧ-104 от 03\.09\.2026 · к оплате 128\s400,50/);
  });
});
