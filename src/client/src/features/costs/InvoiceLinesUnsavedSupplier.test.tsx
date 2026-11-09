// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MutableRefObject } from 'react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceLineView, InvoiceView } from '@/shared/api/invoices';
import { InvoiceLinesTable, type LinesReport } from './InvoiceLinesTable';
import type { LinesStep } from './invoiceSave';
import { HELD_MEMORY_NOTICE, pickByHand, type SupplierChange } from './supplierMatches';
import { emptyDraft } from './invoiceLines';
import { LineMatchChip } from './LineMatchChip';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));
vi.mock('@/shared/api/access', () => ({ NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true }));

/**
 * Поставщик в шапке изменён и не сохранён (issue #1657, change `costs-invoice-lines-unsaved-supplier`, [COST-7.1]) —
 * на настоящей таблице строк: запомненное прежнего не спрашивается и не подставляется (D2), строки, записанные без
 * шапки, своего выбора не запоминают (решение (б)), а записанные после шапки — запоминают, как прежде.
 */

afterEach(cleanup);
beforeEach(() => {
  vi.mocked(apiClient.put).mockReset();
  vi.mocked(apiClient.post).mockReset();
});

const OLD = 'a0000000-0000-4000-8000-0000000000aa';
const NEW = 'b0000000-0000-4000-8000-0000000000bb';
const POSITION = 'c0000000-0000-4000-8000-0000000000cc';

function line(id: string, patch: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id, ordinal: 1, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: 'Кабель силовой', supplierCode: null, unit: 'м', quantity: 1, price: 10, vatRate: null, vatAmount: null,
    amount: 10, note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
    ...patch,
  };
}

function view(supplier: string, lines: InvoiceLineView[], version = 'v1'): InvoiceView {
  return {
    id: 'i1', version, documentTypeId: 't', vatMode: 'Included', unconfirmed: [],
    requisites: { Состояние: 'Черновик', Поставщик: { $ref: 'catalog', entryId: supplier } },
    duplicates: [], sameFile: [], lines,
    readiness: { required: [], gaps: [] }, totals: { count: lines.length },
    payment: { lockedBy: null }, allocation: { document: { parts: [] } },
  } as unknown as InvoiceView;
}

function mount(shown: InvoiceView, supplierChange: SupplierChange | null) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const saver: MutableRefObject<LinesStep | null> = { current: null };
  const reports: LinesReport[] = [];
  render(
    <QueryClientProvider client={qc}>
      <SnackbarProvider>
        <InvoiceLinesTable view={shown} locked={false} saverRef={saver} supplierChange={supplierChange}
          onReport={report => reports.push(report)} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
  return { saver, last: () => reports.at(-1)! };
}

/** Строки, ушедшие записью. */
const sentLines = (call = 0) => (vi.mocked(apiClient.put).mock.calls[call][1] as { lines: Record<string, unknown>[] }).lines;

describe('поставщик в шапке изменён и не сохранён', () => {
  it('запомненное прежнего не спрашивается, перечню названа причина', async () => {
    const { last } = mount(view(OLD, [line('l1')]), { to: NEW });
    fireEvent.change(screen.getByRole('textbox', { name: 'Наименование в счёте, строка 1' }), { target: { value: 'Кабель другой' } });
    fireEvent.blur(screen.getByRole('textbox', { name: 'Наименование в счёте, строка 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));

    await act(async () => {});
    expect(apiClient.post).not.toHaveBeenCalled();
    expect(last().matches).toMatchObject({ hasSupplier: true, supplierPending: true, ready: 0 });
  });

  it('без смены запомненное лежащих строк спрашивается по сохранённому поставщику', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: { items: [] } } as never);
    const { last } = mount(view(OLD, [line('l1')]), null);
    await vi.waitFor(() => expect(apiClient.post).toHaveBeenCalled());
    expect(JSON.stringify(vi.mocked(apiClient.post).mock.calls[0][1])).toContain(OLD);
    expect(last().matches).toMatchObject({ supplierPending: false });
  });

  it('сняли поставщика и не сохранили — запоминать не за кем', () => {
    const { last } = mount(view(OLD, [line('l1')]), { to: null });
    expect(last().matches).toMatchObject({ hasSupplier: false, supplierPending: true });
  });

  it('строки без шапки уходят с «не запоминать», и об этом сказано', async () => {
    const { saver } = mount(view(OLD, [line('l1', { nomenclatureId: POSITION, nomenclatureName: 'Кабель' })]), { to: NEW });
    // Новое наименование при той же позиции — новость: без правила сервер запомнил бы её за прежним поставщиком.
    fireEvent.change(screen.getByRole('textbox', { name: 'Наименование в счёте, строка 1' }), { target: { value: 'Кабель другой' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view(OLD, [line('l1')], 'v2') } as never);

    await act(async () => { expect(await saver.current!(null)).toBe(true); });
    expect(sentLines().map(sent => sent.remember)).toEqual([false, false]);
    expect(await screen.findByText(HELD_MEMORY_NOTICE)).toBeTruthy();
  });

  it('строки без шапки без новостей — «не запоминать» уходит, а сказать не о чем', async () => {
    const { saver } = mount(view(OLD, [line('l1', { nomenclatureId: POSITION, nomenclatureName: 'Кабель' })]), { to: NEW });
    fireEvent.change(screen.getByRole('textbox', { name: 'Количество, строка 1' }), { target: { value: '3' } });
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view(OLD, [line('l1')], 'v2') } as never);

    await act(async () => { expect(await saver.current!(null)).toBe(true); });
    expect(sentLines().map(sent => sent.remember)).toEqual([false]);
    expect(screen.queryByText(HELD_MEMORY_NOTICE)).toBeNull();
  });

  it('шапка записана тем же нажатием — строки запоминают, как прежде', async () => {
    const { saver } = mount(view(OLD, [line('l1', { nomenclatureId: POSITION, nomenclatureName: 'Кабель' })]), { to: NEW });
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view(NEW, [line('l1')], 'v3') } as never);

    await act(async () => { expect(await saver.current!(view(NEW, [line('l1')], 'v2'))).toBe(true); });
    expect(sentLines()[0]).not.toHaveProperty('remember');
  });
});

describe('пометка строки при несохранённой смене поставщика', () => {
  const picked = { ...emptyDraft(), id: 'l1', supplierText: 'Кабель силовой', ...pickByHand(POSITION, 'Кабель') };
  const chip = (props: Partial<Parameters<typeof LineMatchChip>[0]>) => render(
    <LineMatchChip draft={picked} offer={null} fate="remember" supplierId={OLD}
      onCancel={() => {}} onRestore={() => {}} onRemember={() => {}} {...props} />,
  );

  it('«запомнится» говорит, за кем: за поставщиком из шапки', () => {
    chip({ supplierPending: true });
    fireEvent.click(screen.getByRole('button', { name: 'запомнится' }));
    expect(screen.getByText(/выбор запомнится за новым/)).toBeTruthy();
  });

  it('новая пометка «запомнено» предупреждает, что снимется при сохранении', () => {
    const marked = { ...picked, matchedBy: 'm1', match: { id: 'm1', by: 'name' as const, state: 'current' as const, source: 'Кабель силовой', rememberedAt: '', rememberedBy: null } };
    chip({ draft: marked, fate: 'none', supplierPending: true, leaving: true });
    fireEvent.click(screen.getByRole('button', { name: 'запомнено' }));
    expect(screen.getByText(/это запомнено за прежним/)).toBeTruthy();
  });
});
