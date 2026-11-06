import { describe, expect, it, vi } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { saveInOrder } from './invoiceSave';

/** Одна кнопка «Сохранить»: шапка, затем строки (costs-invoice-screen-10-layout, D-10.7). */

const viewAt = (version: string) => ({ id: 'i1', version } as InvoiceView);

describe('saveInOrder', () => {
  it('шапка раньше строк, строки — с видом из ответа шапки', async () => {
    const calls: string[] = [];
    const header = vi.fn(async () => { calls.push('шапка'); return viewAt('v2'); });
    const lines = vi.fn(async (after: InvoiceView | null) => { calls.push(`строки после ${after?.version}`); return true; });

    expect(await saveInOrder(header, lines)).toBe(true);
    expect(calls).toEqual(['шапка', 'строки после v2']);
  });

  it('отказ шапки строк не пишет', async () => {
    const lines = vi.fn(async () => true);
    expect(await saveInOrder(async () => null, lines)).toBe(false);
    expect(lines).not.toHaveBeenCalled();
  });

  it('шапка чистая — строки идут сами, без чужого вида', async () => {
    const lines = vi.fn(async () => true);
    expect(await saveInOrder(null, lines)).toBe(true);
    expect(lines).toHaveBeenCalledWith(null);
  });

  it('отказ строк — не «сохранено»: уйти со счёта после него нельзя', async () => {
    expect(await saveInOrder(async () => viewAt('v2'), async () => false)).toBe(false);
  });

  it('нечего писать — сохранено', async () => {
    expect(await saveInOrder(null, null)).toBe(true);
  });
});
