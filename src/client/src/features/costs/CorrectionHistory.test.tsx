// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { InvoiceView } from '@/shared/api/invoices';
import type { InvoiceBookEntry } from '@/shared/api/invoiceCorrection';
import { CorrectionHistory } from './CorrectionHistory';
import { bookEntryRows, bookKindTitle, bookTotal } from './correction';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * История счёта — секция «Исправления» (E2, issue #1100, PR 6b-2): только у счёта с книгой, новые записи
 * сверху, строки записи — щелчком.
 */

afterEach(cleanup);

const line = (day: string, gross: number) => ({
  constructionId: null, sectionId: null, articleId: null, day, gross, net: gross, vatUnknown: false,
});

const entries: InvoiceBookEntry[] = [
  { seq: 1, kind: 'opening', day: '2026-10-16', at: '2026-10-16T05:00:00Z', author: 'Бухгалтер Иванова',
    reason: null, version: null, gross: 1000, net: 1000, lines: [line('2026-09-10', 1000)] },
  { seq: 2, kind: 'correction', day: '2026-10-16', at: '2026-10-16T05:00:00Z', author: 'Бухгалтер Иванова',
    reason: 'опечатка в цене', version: 1, gross: 50, net: 50, lines: [line('2026-10-16', 50)] },
];

const view = (book: number): InvoiceView => ({ id: 'i1', version: '9', book }) as unknown as InvoiceView;

let asked: string[];

beforeEach(() => {
  asked = [];
  vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
    asked.push(url);
    return { data: url === '/costs/invoices/i1/book' ? entries : [] };
  }) as never);
});

function mount(book: number) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}><CorrectionHistory view={view(book)} /></QueryClientProvider>);
}

describe('секция «Исправления»', () => {
  it('у счёта без книги её нет и историю не спрашивают', () => {
    mount(0);
    expect(screen.queryByText('Исправления')).toBeNull();
    expect(asked).not.toContain('/costs/invoices/i1/book');
  });

  it('новые записи сверху; у исправления — автор, версия, причина и сдвиг со знаком', async () => {
    mount(2);
    const reason = await screen.findByText('опечатка в цене');
    const rows = screen.getAllByRole('row').map(r => r.textContent ?? '');
    const correction = rows.findIndex(r => r.includes('Исправление (версия 1)'));
    const opening = rows.findIndex(r => r.includes('Открытие книги'));
    expect(correction).toBeGreaterThan(-1);
    expect(opening).toBeGreaterThan(correction);
    expect(rows[correction]).toContain('Бухгалтер Иванова');
    expect(rows[correction]).toMatch(/\+50/);

    fireEvent.click(reason);
    expect(await screen.findByText(/Днём записи 16\.10\.2026/)).toBeTruthy();
  });
});

describe('слова истории', () => {
  it('вид записи — словами, незнакомый — как пришёл', () => {
    expect(bookKindTitle('unpayment')).toBe('Отмена оплаты');
    expect(bookKindTitle('новый-вид')).toBe('новый-вид');
  });

  it('итог открытия — сумма, а не прибавка; у сдвига знак явный', () => {
    expect(bookTotal({ kind: 'opening', gross: 1000 }, String)).toBe('1000');
    expect(bookTotal({ kind: 'correction', gross: 50 }, String)).toBe('+50');
    expect(bookTotal({ kind: 'unpayment', gross: -1000 }, String)).toBe('-1000');
  });

  it('строки исправления — группами по дню, открытия — без групп', () => {
    const groups = (rows: ReturnType<typeof bookEntryRows>) => rows.filter(r => r.kind === 'group').map(r => (r.kind === 'group' ? r.title : ''));
    const correction = { ...entries[1], lines: [line('2026-10-16', -300), line('2026-10-02', 300)] };
    expect(groups(bookEntryRows(correction, iso => iso))).toEqual(['Днём записи 2026-10-16', 'Своим днём (период открыт)']);
    expect(groups(bookEntryRows(entries[0], iso => iso))).toEqual([]);
  });
});
