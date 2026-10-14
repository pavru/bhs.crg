import { describe, expect, it } from 'vitest';
import { QK, isInvoiceView } from './invoices';

describe('isInvoiceView', () => {
  it('вид одного счёта — да; списки, очереди и оплата — нет', () => {
    expect(isInvoiceView([QK, '0192-счёт'])).toBe(true);
    expect(isInvoiceView([QK, 'list', null])).toBe(false);
    expect(isInvoiceView([QK, 'queues'])).toBe(false);
    expect(isInvoiceView([QK, '0192-счёт', 'paid'])).toBe(false);
    expect(isInvoiceView(['costs-organizations', 'choice'])).toBe(false);
  });
});
