// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { MutableRefObject } from 'react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import { QK, type InvoiceLineView, type InvoiceView } from '@/shared/api/invoices';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import type { LinesStep } from './invoiceSave';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));
vi.mock('@/shared/api/access', () => ({ NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true }));

/**
 * Правка строк во время их записи (issue #1656, change `costs-invoice-lines-save-race`, [COST-7]) — на настоящей
 * таблице: вид счёта приходит в неё из кэша, как на экране, и ответ записи кладётся в кэш тем же `useInvoiceWrite`.
 */

afterEach(cleanup);
beforeEach(() => vi.mocked(apiClient.put).mockReset());

const A = 'a0000000-0000-4000-8000-000000000001';

function line(id: string, ordinal: number, quantity: number): InvoiceLineView {
  return {
    id, ordinal, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: 'Кабель', supplierCode: null, unit: 'м', quantity, price: 10, vatRate: null, vatAmount: null,
    amount: quantity * 10, note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
  };
}

function view(version: string, lines: InvoiceLineView[]): InvoiceView {
  return {
    id: 'i1', version, documentTypeId: 't', vatMode: 'Included', unconfirmed: [],
    requisites: { Состояние: 'Черновик' }, duplicates: [], sameFile: [], lines,
    readiness: { required: [], gaps: [] }, totals: { count: lines.length },
    payment: { lockedBy: null }, allocation: { document: { parts: [] } },
  } as unknown as InvoiceView;
}

/** Таблица, читающая вид из кэша, — как форма счёта. */
function Screen({ saver }: { saver: MutableRefObject<LinesStep | null> }) {
  const { data } = useQuery({ queryKey: [QK, 'i1'], queryFn: () => new Promise<InvoiceView>(() => {}), staleTime: Infinity });
  return data ? <InvoiceLinesTable view={data} locked={false} saverRef={saver} /> : null;
}

function mount(first: InvoiceView) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData([QK, 'i1'], first);
  // Шаг записи таблица кладёт сюда сама — так его зовёт «Сохранить» сводки.
  const saver: MutableRefObject<LinesStep | null> = { current: null };
  render(<QueryClientProvider client={qc}><SnackbarProvider><Screen saver={saver} /></SnackbarProvider></QueryClientProvider>);
  return saver;
}

/** Ответ записи, который приходит по команде теста. */
function answerLater() {
  let resolve!: (value: { data: InvoiceView }) => void;
  vi.mocked(apiClient.put).mockImplementationOnce(() => new Promise(r => { resolve = r; }) as never);
  return (value: InvoiceView) => act(async () => { resolve({ data: value }); });
}

const quantity = (row: number) => screen.getByRole('textbox', { name: `Количество, строка ${row}` }) as HTMLInputElement;
const unsaved = () => screen.queryByText('(посчитано в форме — строки ещё не сохранены)');

describe('правка строк во время их записи', () => {
  it('набранное во время записи остаётся на экране, несохранённым; вторая запись называет версию ответа', async () => {
    const saver = mount(view('v1', [line(A, 1, 1)]));
    fireEvent.change(quantity(1), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));

    const answer = answerLater();
    let first!: Promise<boolean>;
    act(() => { first = saver.current!(null); });
    await vi.waitFor(() => expect(apiClient.put).toHaveBeenCalledTimes(1));
    const sent = vi.mocked(apiClient.put).mock.calls[0][1] as { lines: Record<string, unknown>[] };
    const newId = sent.lines[1].newId as string;
    expect(newId).toMatch(/^[0-9a-f-]{36}$/);

    // Запись идёт — человек ставит курсор в новую строку и набирает.
    quantity(2).focus();
    fireEvent.change(quantity(2), { target: { value: '5' } });

    await answer(view('v2', [line(A, 1, 2), { ...line(newId, 2, 0), quantity: null, amount: null }]));
    expect(await first).toBe(true);

    expect(quantity(1).value).toBe('2');
    expect(quantity(2).value).toBe('5');
    // Строка под курсором не перемонтирована: ключ новой строки — её newId.
    expect(document.activeElement).toBe(quantity(2));
    expect(unsaved()).not.toBeNull();

    // Вторая запись: версия из ответа первой, новая строка — уже лежащей (`id`, без `newId`).
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view('v3', [line(A, 1, 2), line(newId, 2, 5)]) } as never);
    await act(async () => { expect(await saver.current!(null)).toBe(true); });
    const [, body, config] = vi.mocked(apiClient.put).mock.calls[1] as [string, { lines: Record<string, unknown>[] }, { headers: Record<string, string> }];
    expect(JSON.stringify(config)).toContain('v2');
    expect(body.lines[1]).toMatchObject({ id: newId });
    expect(body.lines[1]).not.toHaveProperty('newId');
    expect(unsaved()).toBeNull();
  });
});
