import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { K } from './invoiceFields';
import { rejectInvoice, undoVersion } from './invoiceReject';

const view = (state: string) => ({ requisites: { [K.state]: state } }) as unknown as InvoiceView;
const refusal = (status: number) => Object.assign(new Error('отказ'), { response: { status } });

describe('rejectInvoice', () => {
  it('прошедший запрос — «отклонён» с версией ответа (для «Вернуть»), и счёт заново не читается', async () => {
    let read = 0;
    const result = await rejectInvoice(async () => ({ version: 'v7' }), async () => { read++; return view('Отклонён'); });
    expect(result).toEqual({ outcome: 'rejected', version: 'v7' });
    expect(undoVersion(result)).toBe('v7');
    expect(read).toBe(0);
  });

  it('409 при уже отклонённом счёте — не отказ, но и без «Вернуть»: отклонил, возможно, сосед', async () => {
    const result = await rejectInvoice(() => Promise.reject(refusal(409)), async () => view('Отклонён'));
    expect(result.outcome).toBe('already');
    expect(undoVersion(result)).toBeNull();
  });

  it('409 при неотклонённом счёте остаётся отказом', async () => {
    const thrown = refusal(409);
    await expect(rejectInvoice(() => Promise.reject(thrown), async () => view('Черновик'))).rejects.toBe(thrown);
  });

  it('другой отказ уходит как есть, счёт не читается', async () => {
    const thrown = refusal(403);
    let read = 0;
    await expect(rejectInvoice(() => Promise.reject(thrown), async () => { read++; return view('Отклонён'); }))
      .rejects.toBe(thrown);
    expect(read).toBe(0);
  });

  it('счёт не прочитался — наружу уходит исходный отказ, а не сбой чтения', async () => {
    const thrown = refusal(409);
    await expect(rejectInvoice(() => Promise.reject(thrown), () => Promise.reject(new Error('сеть'))))
      .rejects.toBe(thrown);
  });
});
