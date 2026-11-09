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
vi.mock('@/shared/api/access', () => ({
  NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true,
  useCan: () => ({ access: {}, module: () => true, permission: () => true }),
}));

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
  const onReport = (report: LinesReport) => { reports.push(report); };
  const tree = (v: InvoiceView, change: SupplierChange | null) => (
    <QueryClientProvider client={qc}>
      <SnackbarProvider>
        <InvoiceLinesTable view={v} locked={false} saverRef={saver} supplierChange={change} onReport={onReport} />
      </SnackbarProvider>
    </QueryClientProvider>
  );
  const { rerender } = render(tree(shown, supplierChange));
  return { saver, last: () => reports.at(-1)!, again: (v: InvoiceView, change: SupplierChange | null) => rerender(tree(v, change)) };
}

/** Запомненное поставщика для строки «Кабель силовой». */
const remembered = (matchId: string) => ({
  index: 0, matchId, by: 'name', source: 'Кабель силовой', nomenclatureId: POSITION, nomenclatureName: 'Кабель',
  nomenclatureType: null, issue: null, rememberedAt: '2026-11-01T00:00:00Z', rememberedBy: null,
});

/** Новая строка с наименованием — уход с поля спрашивает запомненное. */
function bornLine() {
  fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));
  const name = screen.getByRole('textbox', { name: 'Наименование в счёте, строка 1' });
  fireEvent.change(name, { target: { value: 'Кабель силовой' } });
  fireEvent.blur(name);
}
const askedSupplier = (call = 0) => (vi.mocked(apiClient.post).mock.calls[call][1] as { supplierId: string }).supplierId;

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
    expect(screen.getByText(/Поставщика счёта сменили: это запомнено за прежним/)).toBeTruthy();
  });
});

describe('опыт прежнего поставщика не переживает смену (ревью PR #1830)', () => {
  it('шапку записали прошлым нажатием, а строки нет: подставленное по прежнему снимается при записи', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: { items: [remembered('m-old')] } } as never);
    const { saver, again } = mount(view(OLD, []), null);
    bornLine();
    await screen.findByRole('button', { name: 'запомнено' });

    // Шапка с новым поставщиком записана, строки — нет: правок шапки нет, в виде — новый поставщик.
    again(view(NEW, [], 'v2'), null);
    fireEvent.click(screen.getByRole('button', { name: 'запомнено' }));
    expect(screen.getByText(/Поставщика счёта сменили: это запомнено за прежним/)).toBeTruthy();

    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view(NEW, [line('l1')], 'v3') } as never);
    await act(async () => { expect(await saver.current!(null)).toBe(true); });
    expect(sentLines()[0]).toMatchObject({ matchedBy: null, remember: false });
  });

  it('предложение прежнего поставщика в строке не возвращается после смены', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: { items: [remembered('m-old')] } } as never);
    const { again } = mount(view(OLD, []), null);
    bornLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Отменить подстановку' }));
    expect(screen.getByRole('button', { name: 'вернуть' })).toBeTruthy();

    again(view(NEW, [], 'v2'), null);
    expect(screen.queryByRole('button', { name: 'вернуть' })).toBeNull();
  });

  it('ответ, пришедший после смены поставщика, в строку не ложится', async () => {
    let answer!: (value: unknown) => void;
    vi.mocked(apiClient.post).mockImplementationOnce(() => new Promise(r => { answer = r; }) as never);
    const { again } = mount(view(OLD, []), null);
    bornLine();
    await vi.waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));

    again(view(OLD, []), { to: NEW });
    await act(async () => { answer({ data: { items: [remembered('m-old')] } }); });
    expect(screen.queryByRole('button', { name: 'запомнено' })).toBeNull();
  });

  it('смену отменили, не сохраняя: о строке, набранной за это время, спрашивают по прежнему', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: { items: [remembered('m-old')] } } as never);
    const { again } = mount(view(OLD, []), { to: NEW });
    bornLine();
    await act(async () => {});
    expect(apiClient.post).not.toHaveBeenCalled();

    again(view(OLD, []), null);
    await screen.findByRole('button', { name: 'запомнено' });
    expect(askedSupplier()).toBe(OLD);
  });
});

describe('окно выбора позиции при несохранённой смене (ревью PR #1830)', () => {
  it('подходящие подбираются по поставщику из шапки, и плашка называет его', async () => {
    globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
    vi.mocked(apiClient.post).mockResolvedValue({ data: { lines: [{ items: [], recalledUnusable: null }], items: [] } } as never);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <SnackbarProvider>
          <InvoiceLinesTable view={view(OLD, [line('l1')])} locked={false} supplierChange={{ to: NEW }} supplierName="ООО «Новый»" />
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'выбрать позицию' }));
    await vi.waitFor(() => expect(vi.mocked(apiClient.post).mock.calls.some(([url]) => url === '/costs/nomenclature/candidates')).toBe(true));
    const [, body] = vi.mocked(apiClient.post).mock.calls.find(([url]) => url === '/costs/nomenclature/candidates')!;
    expect((body as { supplierId: string }).supplierId).toBe(NEW);
    expect(await screen.findByText(/ООО «Новый»/)).toBeTruthy();
  });
});
