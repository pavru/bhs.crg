import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import type { PurchaseListRow, PurchaseStatus } from '@/shared/api/purchaseRequests';
import { choicesForInvoice, constructionForRequest } from './invoiceRequestChoices';

function allocation(parts: { constructionId: string | null; articleId?: string }[], allocated = true) {
  return { allocation: { allocated, document: { parts } }, lines: [] } as unknown as InvoiceView;
}

function request(id: string, constructionId: string, status: PurchaseStatus): PurchaseListRow {
  return { id, constructionId, status } as PurchaseListRow;
}

describe('выбор заявки в счёте', () => {
  it('разрешает несколько долей одной стройки', () => {
    expect(constructionForRequest(allocation([{ constructionId: 'a' }, { constructionId: 'a' }]))).toBe('a');
  });

  it('отсекает неполную, смешанную и пустую разноску и статьи', () => {
    expect(constructionForRequest(allocation([{ constructionId: 'a' }], false))).toBeUndefined();
    expect(constructionForRequest(allocation([{ constructionId: 'a' }, { constructionId: 'b' }]))).toBeUndefined();
    expect(constructionForRequest(allocation([{ constructionId: null, articleId: 'stock' }]))).toBeUndefined();
    expect(constructionForRequest(allocation([]))).toBeUndefined();
  });

  it('предлагает поданные заявки только стройки счёта', () => {
    const eligible = request('open', 'a', 'submitted');
    expect(choicesForInvoice([eligible, request('draft', 'a', 'draft'), request('closed', 'a', 'closed'),
      request('other', 'b', 'submitted')], 'a')).toEqual([eligible]);
  });
});
