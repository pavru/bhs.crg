import { describe, expect, it } from 'vitest';
import type { InvoiceLineView, InvoiceView } from '@/shared/api/invoices';
import { emptyDraft, preview, toDrafts, toPayload, type LineDraft } from './invoiceLines';
import { mergeSaved } from './invoiceLinesMerge';

/**
 * Ответ записи строк, слитый с набранным во время записи (issue #1656, D1 (б), [COST-7]).
 */

const A = 'a0000000-0000-4000-8000-000000000001';
const B = 'b0000000-0000-4000-8000-000000000002';

function line(id: string, overrides: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id, ordinal: 1, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: null, supplierCode: null, unit: null, quantity: 1, price: 10, vatRate: null, vatAmount: null,
    amount: 10, note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
    ...overrides,
  };
}

const answer = (lines: InvoiceLineView[]) => ({ id: 'i1', version: 'v2', vatMode: 'Included', lines }) as unknown as InvoiceView;
const at = (drafts: LineDraft[]) => ({ drafts, mode: 'Included' as const });

describe('слияние ответа записи строк с набранным', () => {
  it('ничего не правили — строки из ответа, несохранённого нет', () => {
    const sent = toDrafts([line(A, { quantity: 2, amount: null })], 'Included');
    const saved = answer([line(A, { quantity: 2, amount: 20 })]);
    const merged = mergeSaved(at(sent), at(sent), saved);
    // Строка — из ответа; досчитанная сумма в ней пустая, посчитанной она и показывается (issue #1654).
    expect(merged.drafts[0]).toEqual(toDrafts(saved.lines, 'Included')[0]);
    expect(preview(merged.drafts[0], 'Included').amount).toBe(20);
    expect(merged.dirty).toBe(false);
  });

  it('правка во время записи остаётся на экране и помечена несохранённой', () => {
    const sent = toDrafts([line(A), line(B)], 'Included');
    const typed = [sent[0], { ...sent[1], quantity: '7,' }];
    const merged = mergeSaved(at(sent), at(typed), answer([line(A, { amount: 10 }), line(B, { quantity: 1, amount: 10 })]));

    // Недонабранное «7,» не переписано числом сервера — запятая под курсором на месте.
    expect(merged.drafts[1].quantity).toBe('7,');
    expect(merged.drafts[0].amount).toBe('');
    expect(preview(merged.drafts[0], 'Included').amount).toBe(10);
    expect(merged.dirty).toBe(true);
  });

  it('новая строка получает id, равный своему ключу (newId), и ключ не меняется', () => {
    const born = { ...emptyDraft(), quantity: '3', price: '5' };
    expect(toPayload([born], 'Included')[0]).toMatchObject({ id: null, newId: born.key });

    const merged = mergeSaved(at([born]), at([born]), answer([line(born.key, { quantity: 3, price: 5, amount: 15 })]));
    expect(merged.drafts[0]).toMatchObject({ key: born.key, id: born.key });
    expect(toPayload(merged.drafts, 'Included')[0]).not.toHaveProperty('newId');
    expect(merged.dirty).toBe(false);
  });

  it('новую строку правили во время записи — набранное остаётся, а пишется она дальше лежащей', () => {
    const born = { ...emptyDraft(), quantity: '3', price: '5' };
    const typed = { ...born, quantity: '4' };
    const merged = mergeSaved(at([born]), at([typed]), answer([line(born.key, { quantity: 3, price: 5 })]));
    expect(merged.drafts[0]).toMatchObject({ key: born.key, id: born.key, quantity: '4' });
    expect(merged.dirty).toBe(true);
  });

  it('строка, добавленная во время записи, остаётся новой; удалённая — удалённой', () => {
    const sent = toDrafts([line(A), line(B)], 'Included');
    const added = emptyDraft();
    const merged = mergeSaved(at(sent), at([sent[0], added]), answer([line(A), line(B)]));
    expect(merged.drafts.map(d => d.key)).toEqual([A, added.key]);
    expect(merged.drafts[1].id).toBeNull();
    expect(merged.dirty).toBe(true);
  });

  it('признак потери позиции из ответа — только если позицию с отправки не меняли', () => {
    const sent = toDrafts([line(A, { nomenclatureId: 'n1' }), line(B, { nomenclatureId: 'n1' })], 'Included');
    const now = [{ ...sent[0], quantity: '5' }, { ...sent[1], nomenclatureId: 'n2', nomenclatureName: 'Другая' }];
    const merged = mergeSaved(at(sent), at(now),
      answer([line(A, { nomenclatureId: 'n1', nomenclatureLost: true }), line(B, { nomenclatureId: 'n1', nomenclatureLost: true })]));
    expect(merged.drafts[0].nomenclatureLost).toBe(true);
    expect(merged.drafts[1].nomenclatureLost).toBe(false);
  });

  // Удалённая строка не возвращается под прежним идентификатором (ревью PR #1811) — как у `restoreLine`.
  it('строка, возвращённая «Вернуть» во время записи после своего удаления, встаёт новой со своим ключом', () => {
    const [kept, gone] = toDrafts([line(A), line(B)], 'Included');
    const merged = mergeSaved(at([kept]), at([kept, gone]), answer([line(A)]));
    expect(merged.drafts[1].id).toBeNull();
    expect(merged.drafts[1].key).not.toBe(B);
    expect(toPayload(merged.drafts, 'Included')[1].newId).toBe(merged.drafts[1].key);
  });

  it('«не запоминать» у строки, совпавшей с сервером значениями, несохранённым её не делает', () => {
    const sent = toDrafts([line(A)], 'Included');
    const now = [{ ...sent[0], remember: false }];
    const merged = mergeSaved(at(sent), at(now), answer([line(A)]));
    expect(merged.drafts[0].remember).toBe(false);
    expect(merged.dirty).toBe(false);
  });

  it('режим переключили во время записи — он остаётся, и это несохранённое', () => {
    const sent = toDrafts([line(A)], 'Included');
    const merged = mergeSaved(at(sent), { drafts: sent, mode: 'OnTop' }, answer([line(A)]));
    expect(merged.mode).toBe('OnTop');
    expect(merged.dirty).toBe(true);
  });
});
