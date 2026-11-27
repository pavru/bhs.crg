import type { InvoiceView } from '@/shared/api/invoices';
import type { PurchaseListRow } from '@/shared/api/purchaseRequests';

export function constructionForRequest(invoice: Pick<InvoiceView, 'allocation' | 'lines'>): string | undefined {
  if (!invoice.allocation.allocated) return undefined;
  const parts = [...invoice.allocation.document.parts, ...invoice.lines.flatMap(line => line.allocation.parts)];
  const site = parts[0]?.constructionId;
  return site && parts.every(part => part.constructionId === site && !part.articleId) ? site : undefined;
}

export function choicesForInvoice(items: PurchaseListRow[], constructionId: string | undefined) {
  return items.filter(item => item.constructionId === constructionId && item.status === 'submitted');
}
