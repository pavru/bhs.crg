import { describe, expect, it } from 'vitest';
import type { PostingRow } from '@/shared/api/invoicePayment';
import {
  CALM_ROWS_SHOWN, changedRows, foldedLabel, movedWarning, paidToast, payLabel, rowKey, summarize,
} from './paymentPosting';

const row = (patch: Partial<PostingRow>): PostingRow => ({
  kind: 'construction', constructionId: 'a', name: 'Стройка А', amount: 40_000,
  accountingOn: '2026-09-15', moved: false, note: null, parts: [], ...patch,
});

const movedA = row({ accountingOn: '2026-10-01', moved: true, note: 'у стройки закрыто по 30.09.2026' });
const calmB = row({ constructionId: 'b', name: 'Стройка Б', amount: 60_000 });
const rest = row({ kind: 'remainder', constructionId: null, name: 'Не разнесено', amount: 500 });

describe('расклад оплаты', () => {
  it('без переносов предупреждения нет, и кнопка называется обычно', () => {
    const summary = summarize([calmB, rest]);
    expect(movedWarning(summary, 60_500)).toBeNull();
    expect(payLabel([calmB, rest])).toBe('Отметить оплату');
  });

  it('перенос назван числом строек и суммой, а подпись кнопки меняется', () => {
    const summary = summarize([movedA, calmB]);
    expect(summary.moved).toEqual([movedA]);
    expect(movedWarning(summary, 100_000)).toMatch(/^Переносится: 1 стройка, 40\s000,00\s₽ из 100\s000,00\s₽\.$/);
    expect(payLabel([movedA, calmB])).toBe('Отметить оплату с переносом');
  });

  it('перенесённый остаток — строка по компании, а не стройка', () => {
    const summary = summarize([movedA, { ...rest, moved: true, accountingOn: '2026-10-01' }]);
    expect(movedWarning(summary, null)).toContain('1 стройка и 1 строка по компании');
  });

  it('спокойные строки сверх порога сворачиваются одной, с суммой', () => {
    const calm = Array.from({ length: CALM_ROWS_SHOWN + 2 }, (_, i) => row({ constructionId: `s${i}`, amount: 100 }));
    expect(foldedLabel(calm.slice(CALM_ROWS_SHOWN))).toMatch(/^Ещё 2 строки — 200,00\s₽$/);
  });

  it('тост называет день, с которого перенесённое вошло в затраты', () => {
    expect(paidToast('2026-09-15', [calmB])).toBe('Счёт оплачен 15.09.2026.');
    expect(paidToast('2026-09-15', [movedA, calmB]))
      .toBe('Счёт оплачен 15.09.2026. Перенесено: 1 стройка — в затраты с 01.10.2026.');
  });

  it('после отказа «расклад изменился» подсвечиваются новые строки и строки с другой суммой', () => {
    const before = [row({}), calmB];
    const after = [movedA, { ...calmB, amount: 50_000 }, rest];

    // Стройка А сменила учётную дату — это другая строка расклада; у Б изменилась сумма; остатка не было.
    expect(changedRows(before, after)).toEqual(new Set(after.map(rowKey)));
    expect(changedRows(before, before).size).toBe(0);
  });
});
