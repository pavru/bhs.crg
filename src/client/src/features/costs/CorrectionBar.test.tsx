// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import { QK, useInvoice, type InvoiceView } from '@/shared/api/invoices';
import type { CorrectionPreview } from '@/shared/api/invoiceCorrection';
import { CorrectionBar } from './CorrectionBar';
import { useCorrectionDraft } from './useCorrectionDraft';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * Полоса исправления (E2, issue #1100, PR 6b-1): причина до проверки, запись с отметкой предпросмотра и
 * отказы 409 — по свежему виду счёта, а не по тексту.
 */

afterEach(cleanup);

const view = (overrides: Partial<InvoiceView> = {}): InvoiceView => ({
  id: 'i1', version: '7', requisites: { 'Номер': 'СЧ-1' }, lines: [],
  payment: { lockedBy: 'период закрыт по 30.09.2026' },
  allocation: { document: { parts: [] } },
  ...overrides,
}) as unknown as InvoiceView;

const preview = (day = '2026-10-16'): CorrectionPreview => ({
  version: 2, day, stamp: `отметка-${day}`,
  records: [{ kind: 'correction', day, tolerance: 1, lines: [
    { constructionId: null, sectionId: null, articleId: null, day, gross: -300, net: -250, vatUnknown: false },
  ] }],
});

const conflict = () => Object.assign(new Error('409'), { response: { status: 409, data: { error: 'счёт тем временем изменили' } } });

let fresh: InvoiceView;

beforeEach(() => {
  vi.mocked(apiClient.post).mockReset();
  fresh = view();
  vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
    if (url === '/costs/invoices/i1') return { data: fresh };
    return { data: [] };
  }) as never);
});

/** Форма в миниатюре: вид — из кэша, как у страницы, режим включён с первого кадра. */
function Form() {
  const { data } = useInvoice('i1');
  const draft = useCorrectionDraft();
  const [started, setStarted] = useState(false);
  if (!started) { setStarted(true); draft.start(); }
  if (!data) return null;
  return draft.active
    ? <CorrectionBar view={data} draft={draft} edits={{ 'Номер': 'СЧ-2' }} onResetHeader={() => {}} />
    : <p>режим закрыт</p>;
}

function mount(initial = view()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  qc.setQueryData([QK, 'i1'], initial);
  render(<QueryClientProvider client={qc}><ToastProvider><Form /></ToastProvider></QueryClientProvider>);
}

const check = () => screen.getByRole('button', { name: 'Проверить исправление…' });

function giveReason() {
  fireEvent.change(screen.getByLabelText(/Причина исправления/), { target: { value: 'опечатка в номере' } });
}

describe('полоса исправления', () => {
  it('без причины проверить нельзя — сервер требует её и у предпросмотра', () => {
    mount();
    expect((check() as HTMLButtonElement).disabled).toBe(true);
    giveReason();
    expect((check() as HTMLButtonElement).disabled).toBe(false);
  });

  it('запись — с отметкой показанного предпросмотра и версией черновика', async () => {
    vi.mocked(apiClient.post).mockImplementation((async (url: string) =>
      url.endsWith('/preview') ? { data: preview() } : { data: view({ version: '8' }) }) as never);
    mount();
    giveReason();
    fireEvent.click(check());

    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByText(/Версия 2 уйдёт в историю/);
    expect(within(dialog).getByText(/Днём исправления/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Записать исправление' }));

    await screen.findByText('режим закрыт');
    const [url, body, config] = vi.mocked(apiClient.post).mock.calls[1];
    expect(url).toBe('/costs/invoices/i1/correction');
    expect(body).toMatchObject({ reason: 'опечатка в номере', stamp: 'отметка-2026-10-16', requisites: { 'Номер': 'СЧ-2' } });
    expect(config).toEqual({ headers: { 'If-Match': '7' } });
  });

  it('устарел предпросмотр (та же версия) — пересчитывается сам и ждёт решения', async () => {
    let previews = 0;
    vi.mocked(apiClient.post).mockImplementation((async (url: string) => {
      if (url.endsWith('/preview')) return { data: preview(previews++ === 0 ? '2026-10-16' : '2026-10-17') };
      throw conflict();
    }) as never);
    mount();
    giveReason();
    fireEvent.click(check());
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Записать исправление' }));

    await within(dialog).findByText(/Предпросмотр пересчитан/);
    expect(previews).toBe(2);
    // Второй записи сам он не делает: пересчитанное человек не видел.
    expect(vi.mocked(apiClient.post).mock.calls.filter(([u]) => !String(u).endsWith('/preview'))).toHaveLength(1);
  });

  it('версия сменилась — окно закрывается, полоса говорит «перечитайте»', async () => {
    vi.mocked(apiClient.post).mockImplementation((async () => { throw conflict(); }) as never);
    fresh = view({ version: '8' });
    mount();
    giveReason();
    fireEvent.click(check());

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(await screen.findByText(/исправление на экране собраны по прежнему виду/)).toBeTruthy();
    expect((check() as HTMLButtonElement).disabled).toBe(true);
  });

  it('закрытие отменили — «правьте как обычно» и выход из режима', async () => {
    mount(view({ payment: { lockedBy: null } as InvoiceView['payment'] }));
    expect(screen.getByText(/Исправление больше не нужно/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Выйти из исправления' }));
    await screen.findByText('режим закрыт');
  });
});
