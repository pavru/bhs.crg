import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { K, PARSED, REJECTED } from './invoiceFields';
import { PART_ORDER, attentionOf, initiallyOpen, partFilled, partLine, partOfField, partOfTarget } from './invoiceParts';

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

  it('разноска — когда сходится', () => {
    expect(partFilled('allocation', view({ allocation: { allocated: false } } as unknown as Partial<InvoiceView>), clean)).toBe(false);
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

describe('«Сроки и оплата» — свёрнута, если решать нечего (costs-invoice-terms-collapse, COST-6.2)', () => {
  const noShipment = { [K.shippedOn]: null, [K.deferral]: null, [K.dueDate]: null };

  it('срок не определён, меток нет — свёрнута строкой «срок не определён · не оплачен»', () => {
    const v = view({ requisites: noShipment });
    expect(partFilled('terms', v, clean)).toBe(true);
    expect(initiallyOpen(v, clean).terms).toBe(false);
    expect(partLine('terms', v, clean, () => null)).toBe('срок не определён · не оплачен');
  });

  it('срок определён, меток нет — свёрнута, как и прежде', () => {
    expect(partFilled('terms', view(), clean)).toBe(true);
  });

  it('дата отгрузки со скана, не проверена — раскрыта', () => {
    const v = view({ requisites: { ...noShipment, [K.shippedOn]: '2026-09-05' }, unconfirmed: [K.shippedOn] });
    expect(partFilled('terms', v, clean)).toBe(false);
    expect(initiallyOpen(v, clean).terms).toBe(true);
  });

  it('пункт готовности о поле части — раскрыта; правка поля его снимает', () => {
    const gaps = [{ kind: 'field', label: 'отсрочка не задана', field: K.deferral }];
    const v = view({ requisites: noShipment, readiness: { required: [], gaps } });
    expect(partFilled('terms', v, clean)).toBe(false);
    expect(partFilled('terms', v, { edits: { [K.deferral]: 10 }, linesDirty: false })).toBe(true);
  });

  it('отгрузка раньше даты счёта — раскрыта; у оплаченного — нет; правка даты снимает (ревью PR #1855)', () => {
    const early = view({ requisites: { [K.shippedOn]: '2026-09-01', [K.deferral]: 10, [K.dueDate]: '2026-09-11' } });
    expect(partFilled('terms', early, clean)).toBe(false);
    expect(attentionOf(early, clean).terms).toBe(true);
    expect(partFilled('terms', early, { edits: { [K.shippedOn]: '2026-09-05' }, linesDirty: false })).toBe(true);
    const paid = view({
      requisites: { [K.shippedOn]: '2026-09-01', [K.deferral]: 10, [K.dueDate]: '2026-09-11' },
      payment: { paid: true, paidOn: '2026-09-10' },
    } as unknown as Partial<InvoiceView>);
    expect(partFilled('terms', paid, clean)).toBe(true);
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

describe('поля типа «Счёт» и то, что «Кто и сколько» показывает сверх своих полей (ревью PR #1786)', () => {
  const extra = { extra: ['contract'], attention: false };

  it('метка и пункт готовности у поля типа держат «Ещё» раскрытой', () => {
    expect(initiallyOpen(view({ unconfirmed: ['contract'] }), clean, extra).more).toBe(true);
    const gaps = [{ field: 'contract', label: 'Договор' }] as unknown as NonNullable<InvoiceView['readiness']>['gaps'];
    expect(partFilled('more', view({ readiness: { required: [], gaps } }), clean, extra)).toBe(false);
    // Без знания о поле типа часть свернулась бы и спрятала метку.
    expect(initiallyOpen(view({ unconfirmed: ['contract'] }), clean).more).toBe(false);
  });

  it('пункт готовности о поле типа ведёт в «Ещё», о неизвестном — никуда', () => {
    expect(partOfField('contract', extra.extra)).toBe('more');
    expect(partOfTarget({ place: 'field', key: 'contract' } as never, extra.extra)).toBe('more');
    expect(partOfField('contract')).toBeNull();
  });

  it('метки вне блоков и замечания распознавания держат «Кто и сколько» раскрытой', () => {
    expect(partFilled('who', view(), clean)).toBe(true);
    expect(partFilled('who', view(), clean, { extra: [], attention: true })).toBe(false);
    expect(attentionOf(view(), clean, { extra: [], attention: true }).who).toBe(true);
  });

  it('метка на месте — внимание у её части, у строк и разноски его нет', () => {
    const now = attentionOf(view({ unconfirmed: [K.dueDate, K.basis] }), clean);
    expect(now).toMatchObject({ who: false, lines: false, allocation: false, terms: true, more: true, history: false });
    // Правка поля снимает метку с него — и внимание с части.
    expect(attentionOf(view({ unconfirmed: [K.dueDate] }), { edits: { [K.dueDate]: '2026-10-05' }, linesDirty: false }).terms).toBe(false);
  });
});

describe('строка-итог разноски называет, что состояние значит для денег (ревью PR #1787)', () => {
  const unallocated = { allocation: { allocated: false } } as unknown as Partial<InvoiceView>;
  it('разобран до разноски и отклонён', () => {
    expect(partLine('allocation', view({ ...unallocated, requisites: { [K.state]: PARSED } }), clean, () => null))
      .toBe('разобран до разноски — деньги ни к одной стройке не отнесены');
    expect(partLine('allocation', view({ requisites: { [K.state]: REJECTED } }), clean, () => null))
      .toBe('отклонён — в затраты не входит');
    expect(partLine('allocation', view(unallocated), clean, () => null)).toBe('не разнесён');
  });
});
