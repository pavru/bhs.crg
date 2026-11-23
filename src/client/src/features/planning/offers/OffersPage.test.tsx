// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Offer, OfferLine, OfferRebaseLine, OfferRebasePreview, OfferVersion } from '@/shared/api/planningOfferTypes';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { CreateOfferDialog } from './CreateOfferDialog';
import { OffersPage } from './OffersPage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }));
vi.mock('@/shared/siteFilter/siteFilterContext', async importOriginal => ({ ...await importOriginal<typeof import('@/shared/siteFilter/siteFilterContext')>(),
  useSiteFilter: vi.fn(),
}));
let permissions: string[];
vi.mock('@/shared/api/access', () => ({ useCan: () => ({ permission: (code: string) => permissions.includes(code) }) }));

const line: OfferLine = {
  id: 'l1', order: 1, number: '4.1', name: 'Лоток', lsrSection: { code: '01', name: 'Сети' }, volume: 1.234567, unit: '100 м',
  workTypeId: null, kind: 'work', ownMarkup: null, markup: { value: 20, level: 'section' }, price: 123, amount: 321,
  marks: [], cost: { total: 7654321, margin: 6543210 },
};
const revision = { id: 'r1', number: 1, estimateId: 'e1', estimateName: 'Смета ЛСР' };
const newer = { id: 'r2', number: 2 };
const initial: Offer = {
  id: 'o1', number: 'КП-2026-0001', date: '2026-11-23', status: 'draft', currentVersion: 1, sentOn: null,
  version: '79', constructionId: 'c1', customer: null, revision, source: 'estimate', markup: 10,
  vat: { mode: 'on-top', rate: 20 }, money: { linesTotal: 333, vat: 22, total: 355, incomplete: false },
  sections: [{ code: '01', name: 'Сети', lines: 1, markup: 20 }], lines: [line], data: {}, createdAt: '', createdBy: null,
  changedAt: '', priceListDate: null, canBuildSchedule: true, scheduleReason: null, cost: line.cost,
  drift: { code: 'newer-revision', source: revision, current: revision, latest: newer, text: 'Смета изменилась. По ред. 1, в смете есть ред. 2.' },
};

function mount(child = <OffersPage />, path = '/o1') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}>{child}</MemoryRouter></QueryClientProvider>);
}

function serve(read: () => Offer = () => initial, versions: OfferVersion[] = []) {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/offers/o1') return { data: read() } as never;
    if (url === '/plan/offers/o1/versions') return { data: versions } as never;
    throw new Error(`Неожиданный адрес ${url}`);
  });
}

function access(...extra: string[]) { permissions = ['plan.offer.read', ...extra]; }
function click(name: string) { fireEvent.click(screen.getByRole('button', { name })); }
function tab(name: string) { fireEvent.click(screen.getByRole('tab', { name })); }
const conflict = (data: unknown) => ({ response: { status: 409, data } });
const markup = () => screen.getByRole('textbox', { name: 'Наценка КП' }) as HTMLInputElement;

beforeEach(() => {
  access();
  window.matchMedia = vi.fn().mockReturnValue({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.mocked(useSiteFilter).mockReturnValue({ status: 'ready', active: false, visible: () => true } as never);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

describe('КП на живых компонентах', () => {
  it.each([true, false])('реестр строит колонку итога по ответу (%s)', async withMoney => {
    const { money, ...rest } = initial;
    vi.mocked(apiClient.get).mockResolvedValue({ data: { items: [withMoney ? { ...rest, money } : rest] } });
    mount(<OffersPage />, '/');
    await screen.findByText(initial.number);
    expect(!!screen.queryByRole('columnheader', { name: 'Итого' })).toBe(withMoney);
    expect(apiClient.get).not.toHaveBeenCalledWith('/constructions/directory', expect.anything());
  });

  it('без независимого права на себестоимость не рисует колонку и значения даже из кешированного ответа', async () => {
    access('plan.offer.edit');
    serve();
    mount();
    await screen.findByRole('heading', { name: initial.number });
    expect(screen.getByText('20 % · Глава')).toBeTruthy();
    expect(screen.getByText('100 м')).toBeTruthy();
    expect(document.body.textContent?.replace(/\s/g, '')).not.toMatch(/7654321|6543210/);
    expect(screen.queryByRole('columnheader', { name: 'Себестоимость' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Отметить отправленным/ })).toBeNull();
    tab('Наценки');
    expect(markup().value).toBe('10');
  });

  it('сохраняет отдельные наценки главы и строки и перебирает версию только после перечитывания', async () => {
    access('plan.offer.edit');
    let server = initial;
    serve(() => server);
    vi.mocked(apiClient.put).mockRejectedValueOnce(conflict({ error: 'КП тем временем изменили' }));
    mount();
    await screen.findByRole('heading', { name: initial.number });
    tab('Наценки');
    fireEvent.change(markup(), { target: { value: '17,5' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Своя наценка: 4.1 · Лоток' }), { target: { value: '0' } });
    click('Сохранить наценки');
    await screen.findByText(/КП тем временем изменили/);
    expect(markup().value).toBe('17,5');
    expect(apiClient.put).toHaveBeenLastCalledWith('/plan/offers/o1/markups', {
      markup: 17.5, sections: [{ code: '01', markup: 20 }], lines: [{ lineId: 'l1', markup: 0 }],
    }, { headers: { 'If-Match': '79' } });
    server = { ...initial, version: '80', sections: [{ ...initial.sections[0], markup: 30 }] };
    click('Перечитать КП');
    await screen.findByText(/КП перечитано/);
    expect(markup().value).toBe('17,5');
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { ...server, version: '81', markup: 17.5 } });
    click('Сохранить наценки');
    await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/plan/offers/o1/markups', {
      markup: 17.5, sections: [{ code: '01', markup: 30 }], lines: [{ lineId: 'l1', markup: 0 }],
    }, { headers: { 'If-Match': '80' } }));
  });

  it('отправленное КП не имеет ячеек правки, а счётчик новой версии не заменяет историю', async () => {
    access('plan.offer.edit', 'plan.offer.send');
    const sent = { ...initial, currentVersion: 2, status: 'sent' as const, sentOn: '2026-11-23' };
    const old: OfferVersion = { number: 1, status: 'accepted', sentOn: '2026-11-20', total: 210, at: '2026-11-23T11:00:00Z', authorId: null,
      content: { date: '2026-11-20', revisionId: 'r1', source: 'estimate', markup: 20, sections: [], lines: [], vat: { mode: 'none', rate: null },
        money: { total: 210, linesTotal: 210, vat: null, incomplete: false } } };
    serve(() => sent, [old]);
    // Routes внутри OffersPage обычно монтируются на /offers/*.
    mount();
    await screen.findByRole('button', { name: 'Новая версия…' });
    tab('Наценки');
    expect(screen.queryByRole('textbox', { name: 'Наценка КП' })).toBeNull();
    tab('Версии');
    expect(await screen.findByRole('link', { name: 'Открыть версию 1' })).toBeTruthy();
    expect(screen.getByText('Принято')).toBeTruthy();
  });

  it.each([400, 403])('сохраняет введённое при отказе %d', async status => {
    access('plan.offer.edit');
    serve();
    vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status, data: { error: 'Уточните наценку' } } });
    mount();
    await screen.findByRole('heading', { name: initial.number });
    tab('Наценки');
    fireEvent.change(markup(), { target: { value: '19' } });
    click('Сохранить наценки');
    await screen.findByText(/Введённые данные остались в форме/);
    expect(markup().value).toBe('19');
    expect((screen.getByRole('button', { name: 'Сохранить наценки' }) as HTMLButtonElement).disabled).toBe(status === 403);
  });

  it('заменяет устаревший предпросмотр и ждёт отдельного подтверждения', async () => {
    access('plan.offer.edit');
    const added: OfferRebaseLine = { revisionRowId: 'new', lineId: null, number: '8.1', name: 'Новая строка', lsrSection: null,
      volume: 2, unit: 'м', workTypeId: null, kind: 'work', ownMarkup: null, price: 42, amount: 84 };
    const preview: OfferRebasePreview = { source: revision, target: newer, version: '79', fingerprint: 'old', changed: [],
      added: [], removed: [], unmatched: [], unmatchedSectionMarkups: [] };
    vi.mocked(apiClient.get).mockImplementation(async (url: string) => ({ data: url.endsWith('/rebase-preview') ? preview : initial }) as never);
    const refreshed = { ...preview, target: { id: 'r3', number: 3 }, fingerprint: 'new', added: [added] };
    vi.mocked(apiClient.post).mockRejectedValueOnce(conflict({ error: 'Смета изменилась после предпросмотра', preview: refreshed }));
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: { offer: { ...initial, version: '80', drift: null }, removed: [] } });
    mount();
    await screen.findByRole('heading', { name: initial.number });
    click('Обновить по смете…');
    await screen.findByText(/Редакция 1 → редакция 2/);
    expect(apiClient.get).toHaveBeenCalledWith('/plan/offers/o1/rebase-preview', expect.objectContaining({ params: { revisionId: 'r2' } }));
    click('Обновить черновик');
    await screen.findByText('8.1 · Новая строка');
    expect(apiClient.post).toHaveBeenCalledTimes(1);
    click('Обновить черновик');
    await waitFor(() => expect(apiClient.post).toHaveBeenLastCalledWith('/plan/offers/o1/rebase',
      { revisionId: 'r3', fingerprint: 'new' }, { headers: { 'If-Match': '79' } }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('создаёт черновик из названной редакции и ведёт к ответу сервера', async () => {
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: initial });
    const onCreated = vi.fn();
    mount(<CreateOfferDialog revision={{ id: 'r7', number: 7 }} estimateName="Смета" canOpen onClose={() => {}} onCreated={onCreated} />);
    const modal = screen.getByRole('dialog', { name: 'Сделать КП из редакции 7' });
    fireEvent.change(within(modal).getByRole('textbox', { name: 'Наценка КП' }), { target: { value: '8' } });
    fireEvent.click(within(modal).getByRole('button', { name: 'Создать черновик' }));
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(initial));
    expect(apiClient.post).toHaveBeenCalledWith('/plan/offers', expect.objectContaining({ revisionId: 'r7', markup: 8 }));
  });
});
