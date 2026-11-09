// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { MutableRefObject } from 'react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import { QK, type AllocationPartView, type InvoiceLineView, type InvoiceView } from '@/shared/api/invoices';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { cascadeTargets } from './lineDeletion';
import type { LinesStep } from './invoiceSave';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));
vi.mock('@/shared/api/access', () => ({ NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true }));

/**
 * Удаление строки с сохранённой разноской (#1655, change `costs-invoice-line-delete-allocation`, [COST-7] [COST-12]) —
 * на настоящей таблице: окно называет разноску до записи, отказ оставляет строку, строка без разноски уходит сразу.
 */

afterEach(cleanup);
beforeEach(() => vi.mocked(apiClient.put).mockReset());

const A = 'a0000000-0000-4000-8000-000000000001';
const B = 'b0000000-0000-4000-8000-000000000002';

function part(overrides: Partial<AllocationPartView>): AllocationPartView {
  return {
    id: `p-${Math.random()}`, ordinal: 1, constructionId: 'c1', constructionName: 'Школа № 12', sectionId: null, sectionName: null,
    articleId: null, articleName: null, targetLost: false, targetIssue: null, quantity: null, amount: null,
    rounding: 0, discrepancy: 0, mismatched: false, ...overrides,
  };
}

function line(id: string, ordinal: number, text: string, parts: AllocationPartView[]): InvoiceLineView {
  return {
    id, ordinal, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: text, supplierCode: null, unit: 'м', quantity: 100, price: 184, vatRate: null, vatAmount: null,
    amount: 18_400, note: null,
    allocation: { mode: parts.length ? 'quantity' : 'none', parts, unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
  } as InvoiceLineView;
}

const allocated = line(A, 1, 'Кабель ВВГнг(А)-LS 3×2,5', [
  part({ sectionId: 's1', sectionName: 'ЭОМ', quantity: 40, amount: 7_360 }),
  part({ constructionId: 'c2', constructionName: 'Детский сад «Радуга»', sectionName: 'Наружное освещение', sectionId: 's2', quantity: 60, amount: 11_040 }),
]);
const plain = line(B, 2, 'Труба без разноски', []);

function view(lines: InvoiceLineView[]): InvoiceView {
  return {
    id: 'i1', version: 'v1', documentTypeId: 't', vatMode: 'Included', unconfirmed: [],
    requisites: { Состояние: 'Черновик' }, duplicates: [], sameFile: [], lines,
    readiness: { required: [], gaps: [] }, totals: { count: lines.length },
    payment: { lockedBy: null }, allocation: { document: { parts: [] } },
  } as unknown as InvoiceView;
}

const LOCK = ['test-lock'];
/** Новые данные запроса — и дождаться рассылки: TanStack Query рассылает таймером, а не сразу. */
const put = (key: readonly unknown[], data: unknown) => act(async () => {
  qc.setQueryData(key, data);
  await new Promise(resolve => setTimeout(resolve, 0));
});

function Screen({ saver }: { saver: MutableRefObject<LinesStep | null> }) {
  const { data } = useQuery({ queryKey: [QK, 'i1'], queryFn: () => new Promise<InvoiceView>(() => {}), staleTime: Infinity });
  const { data: locked } = useQuery({ queryKey: LOCK, queryFn: () => false, initialData: false, staleTime: Infinity });
  return data ? <InvoiceLinesTable view={data} locked={false} readOnly={locked} saverRef={saver} /> : null;
}

let qc: QueryClient;

function mount() {
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  qc.setQueryData([QK, 'i1'], view([allocated, plain]));
  const saver: MutableRefObject<LinesStep | null> = { current: null };
  render(<QueryClientProvider client={qc}><SnackbarProvider><Screen saver={saver} /></SnackbarProvider></QueryClientProvider>);
  return saver;
}

const text = (row: number) => screen.queryByRole('textbox', { name: `Наименование в счёте, строка ${row}` }) as HTMLInputElement | null;
const bin = (row: number) => screen.getByRole('button', { name: `Удалить строку ${row}` });

describe('удаление строки с разноской (#1655)', () => {
  it('до записи названы обе стройки с долями и суммами и сказано, что разноску не вернуть; отказ оставляет строку', async () => {
    mount();
    // Фокус — не на корзине: щелчок мышью в Safari и jsdom кнопку не фокусирует. Вернуть его на корзину обязана сетка.
    text(2)!.focus();
    fireEvent.click(bin(1));
    const dialog = screen.getByRole('dialog', { name: 'Удалить строку 1?' });
    const cascade = within(dialog).getByRole('list', { name: 'Разноска строки' });
    expect(within(cascade).getAllByRole('listitem').map(li => li.textContent)).toEqual([
      expect.stringMatching(/^Школа № 12ЭОМ40 м7\s360,00\s₽$/),
      expect.stringMatching(/^Детский сад «Радуга»Наружное освещение60 м11\s040,00\s₽$/),
    ]);
    expect(dialog.textContent).toContain('Кабель ВВГнг(А)-LS 3×2,5');
    expect(dialog.textContent).toMatch(/100 м · 18\s400,00\s₽/);
    expect(dialog.textContent).toContain('Разнесена на 2 цели');
    expect(dialog.textContent).toContain('После сохранения её не вернуть');
    // Фокус — на «Отмена»: случайный второй Enter ничего не удаляет.
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Отмена' }));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(text(1)?.value).toBe('Кабель ВВГнг(А)-LS 3×2,5');
    expect(text(2)?.value).toBe('Труба без разноски');
    // Отказ возвращает фокус на корзину той же строки (D2).
    await vi.waitFor(() => expect(document.activeElement).toBe(bin(1)));
  });

  it('таблицу заперли, пока окно открыто: окно закрывается само, строка остаётся (ревью #1833)', async () => {
    mount();
    fireEvent.click(bin(1));
    expect(screen.getByRole('dialog', { name: 'Удалить строку 1?' })).toBeTruthy();
    await put(LOCK, true);
    expect(screen.queryByRole('dialog')).toBeNull();
    // Отперли — вопрос сам не возвращается: его больше нет; строка на месте.
    await put(LOCK, false);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(text(1)?.value).toBe('Кабель ВВГнг(А)-LS 3×2,5');
  });

  it('строки перечитались, пока окно открыто: номер в окне — нынешний, подтверждение удаляет ту же строку', async () => {
    mount();
    fireEvent.click(bin(1));
    // Сосед записал строки в другом порядке; черновик не тронут — таблица берёт перечитанные.
    await put([QK, 'i1'], { ...view([{ ...plain, ordinal: 1 }, { ...allocated, ordinal: 2 }]), version: 'v2' });
    const dialog = screen.getByRole('dialog', { name: 'Удалить строку 2?' });
    await act(async () => { fireEvent.click(within(dialog).getByRole('button', { name: 'Удалить строку 2' })); });
    expect(text(1)?.value).toBe('Труба без разноски');
    expect(text(2)).toBeNull();
  });

  it('подтверждение убирает строку без «Вернуть», и запись строк её уже не несёт', async () => {
    const saver = mount();
    fireEvent.click(bin(1));
    await act(async () => {
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Удалить строку 1' }));
    });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(text(1)?.value).toBe('Труба без разноски');
    expect(text(2)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Вернуть' })).toBeNull();
    // Фокус — в поле строки, вставшей на место, а не на её корзине (D2).
    const focused = document.activeElement as HTMLElement;
    expect(focused.closest('tr')?.getAttribute('data-row')).toBe('0');
    expect(focused.closest('[data-cell]')).not.toBeNull();

    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: view([{ ...plain, ordinal: 1 }]) } as never);
    await act(async () => { expect(await saver.current!(null)).toBe(true); });
    const body = vi.mocked(apiClient.put).mock.calls[0][1] as { lines: Record<string, unknown>[] };
    expect(body.lines.map(l => l.id)).toEqual([B]);
  });

  it('строка без разноски удаляется одним действием — без окна, со снэкбаром «Вернуть» (D-13.2)', () => {
    mount();
    fireEvent.click(bin(2));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(text(2)).toBeNull();
    expect(screen.getByText('Строка 2 удалена.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Вернуть' })).toBeTruthy();
  });
});

describe('cascadeTargets — цели разноски словами, как в матрице', () => {
  it('строка суммой: доля — сумма, второй суммы нет; удалённая стройка названа', () => {
    const delivery = {
      ...line(A, 1, 'Доставка', [
        part({ sectionName: 'ЭОМ', sectionId: 's1', amount: 2_400 }),
        part({ constructionName: 'Корпус Б', targetLost: true, amount: 2_400 }),
      ]),
      quantity: null, unit: null,
    };
    delivery.allocation = { ...delivery.allocation, mode: 'amount' };
    expect(cascadeTargets(delivery)).toEqual([
      { target: 'Школа № 12', detail: 'ЭОМ', troubled: false, share: expect.stringMatching(/^2\s400,00\s₽$/), amount: '' },
      { target: 'Корпус Б', detail: 'стройка удалена', troubled: true, share: expect.stringMatching(/^2\s400,00\s₽$/), amount: '' },
    ]);
  });

  it('статья вне строек названа статьёй, а не стройкой', () => {
    const stock = line(A, 1, 'Автомат', [
      part({ constructionId: null, constructionName: null, articleId: 'w', articleName: 'Склад', quantity: 10, amount: 3_450 }),
    ]);
    expect(cascadeTargets(stock)[0]).toMatchObject({ target: 'Склад', detail: 'Статья вне строек', share: '10 м' });
  });
});
