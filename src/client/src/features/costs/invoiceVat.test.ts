import { describe, expect, it } from 'vitest';
import type { InvoiceLineView, InvoiceView, VatMode } from '@/shared/api/invoices';
import cases from './invoiceLineCases.json';
import { correctionBody } from './correction';
import {
  emptyDraft, guessRoles, parseTable, pastedWithoutVat, preview, toDrafts, toPayload, totals, type LineDraft,
} from './invoiceLines';
import { modeConsequences, modeHint, reconcile, savedExplains, withMode } from './invoiceVat';

/** Режим НДС в форме счёта (issue #1499, #1500). */

const format = (value: number) => `#${value}`;

function line(overrides: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id: 'l1', ordinal: 1, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: 'Кабель', supplierCode: null, unit: 'м', quantity: null, price: null, vatRate: null,
    vatAmount: null, amount: null, note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
    ...overrides,
  } as InvoiceLineView;
}

const draft = (overrides: Partial<LineDraft>): LineDraft => ({ ...emptyDraft(), ...overrides });
const text = (value: number | undefined) => (value === undefined ? '' : String(value).replace('.', ','));

describe('общий набор примеров с сервером (сторож С7)', () => {
  // Тот же файл считает InvoiceLineCasesTests на сервере: разойдутся правила — краснеет тот, кто отстал.
  it.each(cases.cases.map(c => [c.name, c] as const))('%s', (_, c) => {
    const shown = preview(draft({
      quantity: text(c.quantity), price: text(c.price), vatRate: text(c.rate), vatAmount: text(c.vat),
      amount: text(c.paper),
    }), c.mode as VatMode);
    expect(shown).toEqual(c.expect);
  });
});

describe('круг «прочитал → отправил без правок» у счёта сверху', () => {
  // Сервер отдаёт amount С НДС; взятый в черновик, он уехал бы бумагой, и НДС прибавился бы второй раз.
  const saved = line({ netAmount: 1000, amount: 1220, vatRate: 22, vatAmount: 220 });

  it('в правке строк: бумажная сумма уезжает полем netAmount и равна прочитанной', () => {
    const [sent] = toPayload(toDrafts([saved], 'OnTop'), 'OnTop');
    expect(sent).toMatchObject({ netAmount: '1000' });
    expect(sent).not.toHaveProperty('amount');
  });

  it('«в том числе» — полем amount, как прежде', () => {
    const [sent] = toPayload(toDrafts([line({ amount: 1220, vatRate: 22 })], 'Included'), 'Included');
    expect(sent).toMatchObject({ amount: '1220' });
    expect(sent).not.toHaveProperty('netAmount');
  });

  it('в теле исправления — тем же полем: режим берётся у счёта', () => {
    const view = { id: 'i1', vatMode: 'OnTop', lines: [saved], requisites: {}, allocation: { document: { parts: [] } } } as unknown as InvoiceView;
    const body = correctionBody(view, { reason: 'опечатка', edits: {}, lines: null, allocation: null });
    expect(body.lines[0]).toMatchObject({ netAmount: '1000' });
    expect(body.lines[0]).not.toHaveProperty('amount');
  });
});

describe('смена режима в черновике', () => {
  it('бумажная сумма остаётся, НДС строк со ставкой пересчитается, без ставки — из бумаги', () => {
    const [rated, silent] = withMode([
      draft({ amount: '1220', vatRate: '22', vatAmount: '220' }),
      draft({ amount: '500', vatAmount: '50' }),
    ]);
    expect(rated).toMatchObject({ amount: '1220', vatAmount: '' });
    expect(preview(rated, 'OnTop')).toEqual({ amount: 1220, vat: 268.4, gross: 1488.4 });
    expect(silent.vatAmount).toBe('50');
  });

  it('флажок поставили и сняли — НДС из бумаги возвращается, а не заменяется расчётным (ревью PR #1508)', () => {
    const paper = draft({ amount: '1220', vatRate: '22', vatAmount: '219,99' });
    const [back] = withMode(withMode([paper]));
    expect(back.vatAmount).toBe('219,99');
    expect(back.vatAside).toBeUndefined();
  });
});

describe('полоса сверки', () => {
  const view = (lines: LineDraft[], mode: VatMode, vat: unknown, total: unknown) =>
    reconcile(totals(lines, mode), vat, total, format);
  const labels = (r: ReturnType<typeof reconcile>) => r.items.map(i => `${i.label}${i.value ?? ''}`);
  const rated = [draft({ amount: '1000', vatRate: '22' }), draft({ amount: '500', vatRate: '22' })];
  const silent = [draft({ amount: '1000' }), draft({ amount: '500' })];

  it('«в том числе» — как прежде, и совпадение сказано, а не молчит', () => {
    const r = view([draft({ amount: '1830', vatRate: '22' })], 'Included', 330, 1830);
    expect(labels(r)).toEqual(['Сумма строк: 1830', 'в том числе НДС: #330', 'к оплате по счёту: 1830']);
    expect(r).toMatchObject({ difference: null, agrees: true });
  });

  it('сверху, НДС у каждой строки — порядком бумаги: без НДС · НДС · всего с НДС · к оплате', () => {
    const r = view(rated, 'OnTop', null, 1830);
    expect(labels(r)).toEqual([
      'Сумма строк без НДС: 1500', 'НДС по строкам: 330', 'всего с НДС: 1830', 'к оплате по счёту: 1830',
    ]);
    expect(r.agrees).toBe(true);
  });

  it('сверху, строки об НДС молчат — НДС из реквизитов; совпало — но «сходится» не говорит: ставка нужна строкам', () => {
    const r = view(silent, 'OnTop', 330, 1830);
    expect(labels(r).slice(0, 4)).toEqual([
      'Сумма строк без НДС: 1500', 'НДС из реквизитов: 330', 'всего с НДС: 1830', 'к оплате по счёту: 1830',
    ]);
    // Сервер у строки без ставки суммы с НДС не знает, и «Разобран» откажет: зелёное «сходится» спорило бы с ним.
    expect(r).toMatchObject({ difference: null, agrees: false });
    expect(r.items.at(-1)).toMatchObject({ tone: 'warning', label: expect.stringContaining('впишите ставку НДС в строках') });
  });

  it('сверху, НДС не указан нигде — «не посчитать», и ложного расхождения на весь налог нет', () => {
    const r = view(silent, 'OnTop', null, 1830);
    expect(r.items.find(i => i.tone === 'warning')?.label).toContain('НДС не указан — всего с НДС не посчитать');
    expect(labels(r).some(l => l.startsWith('всего с НДС'))).toBe(false);
    expect(r).toMatchObject({ difference: null, agrees: false });
  });

  it('сверху, НДС у части строк и не в реквизитах — сколько известно и у скольких не указан', () => {
    const r = view([rated[0], silent[1]], 'OnTop', null, 1830);
    expect(r.items.find(i => i.tone === 'warning')?.label)
      .toContain('НДС по строкам: #220, у 1 строки не указан — всего с НДС не посчитать');
    expect(r.difference).toBeNull();
  });

  it('сверху с расхождением — число расхождения от суммы с НДС', () => {
    expect(view(rated, 'OnTop', null, 1900).difference).toBe(70);
  });
});

describe('вставка из буфера', () => {
  const both = parseTable('Наименование\tКол-во\tЦена\tСумма без НДС\tСумма с НДС\nКабель\t2\t500\t1000\t1220');

  it('«Сумма без НДС | Сумма с НДС» — сумма строки по режиму, а не всегда левая', () => {
    expect(guessRoles(both, 'Included')).toEqual(['supplierText', 'quantity', 'price', 'skip', 'amount']);
    expect(guessRoles(both, 'OnTop')).toEqual(['supplierText', 'quantity', 'price', 'amount', 'skip']);
  });

  it('шапка «Цена без НДС» — цены без НДС; одна сумма «без НДС» — тоже; обе суммы — догадки нет', () => {
    expect(pastedWithoutVat(parseTable('Товар\tКол-во\tЦена без НДС\tСумма\nКабель\t2\t500\t1000'))).toBe(true);
    expect(pastedWithoutVat(parseTable('Товар\tКол-во\tСумма без НДС\nКабель\t2\t1000'))).toBe(true);
    expect(pastedWithoutVat(both)).toBe(false);
    expect(pastedWithoutVat(parseTable('Кабель\t2\t500\t1000'))).toBe(false);
  });
});

describe('последствия смены режима', () => {
  const allocated = (ordinal: number, mode: 'quantity' | 'amount') => line({
    id: `l${ordinal}`, ordinal,
    allocation: { mode, parts: [{} as never], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
  });

  const drafts = (view: InvoiceView) => toDrafts(view.lines, 'Included');

  it('разноска суммой — строками; по количеству — пересчитается сама', () => {
    const view = { lines: [allocated(1, 'quantity'), allocated(2, 'amount'), allocated(3, 'amount')] } as InvoiceView;
    expect(modeConsequences(view, drafts(view), false)).toBe('Суммы строк с НДС изменятся, разноска по количеству '
      + 'пересчитается сама, разноска суммой у строк 2, 3 разойдётся — проверьте её после сохранения.');
  });

  it('номера — как на экране: строка, удалённая в черновике, их сдвигает (ревью PR #1508)', () => {
    const view = { lines: [allocated(1, 'quantity'), allocated(2, 'quantity'), allocated(3, 'amount')] } as InvoiceView;
    const [first, , third] = drafts(view);
    expect(modeConsequences(view, [first, third], false)).toContain('у строки 2 разойдётся');
  });

  it('у разобранного с разноской — счёт может вернуться в черновик', () => {
    const view = { lines: [allocated(2, 'amount')] } as InvoiceView;
    expect(modeConsequences(view, drafts(view), true)).toContain('Если разноска не сойдётся, счёт вернётся в черновик.');
  });
});

describe('подсказка «похоже, цены без НДС» (#1500)', () => {
  it('догадка — только сервера, по пункту готовности; режима сама не меняет', () => {
    const view = { readiness: { required: [], gaps: [{ kind: 'total', label: '…', vatExplains: 'onTop' }] } } as unknown as InvoiceView;
    expect(savedExplains(view)).toBe('onTop');
    expect(savedExplains({ readiness: { required: [], gaps: [] } } as unknown as InvoiceView)).toBeNull();
    expect(modeHint('onTop')).toMatchObject({ action: 'Считать НДС сверху', to: 'OnTop' });
    expect(modeHint('included')).toMatchObject({ action: 'Считать НДС в сумме', to: 'Included' });
  });
});
