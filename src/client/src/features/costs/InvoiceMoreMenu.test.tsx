// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceView } from '@/shared/api/invoices';
import { InvoiceDeleteContext } from './invoiceDelete';
import { InvoiceMoreMenu } from './InvoiceMoreMenu';
import type { RejectResult } from './invoiceReject';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));

/**
 * «⋮ → Отклонить» без окна (costs-invoice-screen-13-undo, D-13.1, задача 1.1) и подпись удаления нового
 * черновика без многоточия (D-13.5, задача 3.4).
 */

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const view = { id: 'i1', version: 'v1', requisites: { Номер: '104', Состояние: 'Черновик' }, payment: { paid: false } } as unknown as InvoiceView;

function mount(props: Partial<Parameters<typeof InvoiceMoreMenu>[0]> = {}, entry: { now: boolean } | null = null) {
  const ask = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <SnackbarProvider>
        <InvoiceDeleteContext.Provider value={entry && { ...entry, ask }}>
          <InvoiceMoreMenu view={view} canChange {...props} />
        </InvoiceDeleteContext.Provider>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
  return ask;
}

const openMenu = () => fireEvent.keyDown(screen.getByRole('button', { name: 'Ещё' }), { key: 'Enter' });

describe('«⋮ → Отклонить»', () => {
  it('без окна: запрос ушёл сразу, снэкбар «отклонён» с «Вернуть», который построила страница', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: { ...view, version: 'v2', requisites: { ...view.requisites, Состояние: 'Отклонён' } } });
    const undo = vi.fn();
    let got: RejectResult | null = null;
    mount({ onRejected: async (send, say) => { const r = await send(); got = r; say(r, undo); return r; } });
    openMenu();
    const item = await screen.findByRole('menuitem', { name: /Отклонить/ });
    expect(item.textContent).not.toContain('…');
    expect(item.textContent).toContain('останется в реестре');
    await act(async () => { fireEvent.click(item); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(vi.mocked(apiClient.post).mock.calls[0][0]).toBe('/costs/invoices/i1/rejected');
    expect(got).toEqual({ outcome: 'rejected', version: 'v2' });
    expect(await screen.findByText('Счёт № 104 отклонён.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Вернуть' }));
    expect(undo).toHaveBeenCalledTimes(1);
  });

  it('отказ — словами сервера, счёт не отклонён', async () => {
    vi.mocked(apiClient.post).mockRejectedValue({ response: { status: 403, data: { error: 'Нет права' } } });
    mount();
    openMenu();
    await act(async () => { fireEvent.click(await screen.findByRole('menuitem', { name: /Отклонить/ })); });
    expect(await screen.findByText(/Нет права/)).toBeTruthy();
  });
});

describe('«⋮ → Удалить»', () => {
  it('новый черновик — «Удалить черновик» без многоточия; иначе — «Удалить счёт…» с окном', async () => {
    const ask = mount({}, { now: true });
    openMenu();
    await act(async () => { fireEvent.click(await screen.findByRole('menuitem', { name: 'Удалить черновик' })); });
    expect(ask).toHaveBeenCalledWith('menu');
    cleanup();
    mount({}, { now: false });
    openMenu();
    expect(await screen.findByRole('menuitem', { name: 'Удалить счёт…' })).toBeTruthy();
  });
});
