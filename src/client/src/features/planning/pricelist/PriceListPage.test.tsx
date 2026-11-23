// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Offer, OfferLine } from '@/shared/api/planningOfferTypes';
import type { PriceListEntry, PriceListWork } from '@/shared/api/planningPriceList';
import { PriceListPage } from './PriceListPage';
import { PriceListOfferDialog } from './PriceListOfferDialog';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), put: vi.fn(), delete: vi.fn(), post: vi.fn() } }));
let rights: string[];
vi.mock('@/shared/api/access', () => ({ useCan: () => ({ permission: (code: string) => rights.includes(code) }) }));

const date = '2026-11-23';
const effective: PriceListEntry = { id: 'p1', workTypeId: 'w1', unitId: 'm', unit: 'м', validFrom: date, price: 350, version: '8' };
const past: PriceListEntry = { ...effective, id: 'p0', unitId: 'piece', unit: 'шт', validFrom: '2026-05-01', price: 190, version: '4' };
const work: PriceListWork = { workTypeId: 'w1', code: 'А1', name: 'Монтаж лотка', unit: 'м', unitId: 'm', archived: false,
  effective, history: [effective, past] };

function mount(child = <PriceListPage />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><MemoryRouter>{child}</MemoryRouter></QueryClientProvider>);
}

function serve(read: () => PriceListWork = () => work, catalog = true) {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/pricelist') return { data: { date, items: [read()] } } as never;
    if (url === '/constructions/directory') return { data: [{ id: 'c1', name: 'Стройка 1' }] } as never;
    if (url === '/work-types' && catalog) return { data: [{ id: 'w2', displayName: 'Настройка',
      data: { Код: 'А2', ЕдиницаИзмерения: { entryId: 'piece', displayName: 'шт' } } }] } as never;
    throw new Error(`Неожиданный запрос ${url}`);
  });
}

const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;
const click = (name: string) => fireEvent.click(button(name));
const price = () => screen.getByRole('textbox', { name: 'Цена за единицу' }) as HTMLInputElement;

async function history() {
  fireEvent.click(await screen.findByRole('rowheader', { name: /Монтаж лотка/ }));
  return screen.findByRole('dialog', { name: 'История цен' });
}

async function select(label: string, name: RegExp) {
  await waitFor(() => expect((screen.getByRole('combobox', { name: label }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.keyDown(screen.getByRole('combobox', { name: label }), { key: 'Enter' });
  fireEvent.click(screen.getByRole('option', { name }));
}

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  rights = ['plan.offer.read'];
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.resetAllMocks(); });

it('показывает цены и сохранённую единицу истории без действий правки', async () => {
  serve();
  mount();
  const dialog = await history();
  expect(within(dialog).getByText('шт')).toBeTruthy();
  expect(within(dialog).getByText('Действует')).toBeTruthy();
  expect(within(dialog).queryByRole('button', { name: /Изменить|Снять|Новая цена/ })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Добавить вид в прайс-лист…' })).toBeNull();
  expect(apiClient.get).not.toHaveBeenCalledWith('/work-types', expect.anything());
});

it('даёт править существующую запись без справочника и сохраняет ввод после 409', async () => {
  rights.push('plan.pricelist.edit');
  let current = work;
  serve(() => current, false);
  vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status: 409, data: { error: 'Строку уже изменили.' } } }).mockResolvedValue({});
  mount();
  await screen.findByText(/core.catalog.read/);
  await history();
  click('Изменить цену с 23.11.2026');
  fireEvent.change(price(), { target: { value: '375,50' } });
  click('Сохранить цену');
  await screen.findByText('Строку уже изменили.');
  expect(price().value).toBe('375,50');
  expect(button('Сохранить цену').disabled).toBe(true);
  current = { ...work, history: [{ ...effective, price: 360, version: '9' }, past] };
  click('Перечитать цену');
  await screen.findByText(/Цена в прайс-листе теперь/);
  expect(price().value).toBe('375,50');
  click('Сохранить цену');
  await screen.findByRole('dialog', { name: 'История цен' });
  expect(apiClient.put).toHaveBeenNthCalledWith(1, '/plan/pricelist/w1', { validFrom: date, price: 375.5 }, { headers: { 'If-Match': '8' } });
  expect(apiClient.put).toHaveBeenNthCalledWith(2, '/plan/pricelist/w1', { validFrom: date, price: 375.5 }, { headers: { 'If-Match': '9' } });
  click('Снять цену с 01.05.2026');
  vi.mocked(apiClient.delete).mockResolvedValue({});
  click('Снять цену');
  await waitFor(() => expect(apiClient.delete).toHaveBeenCalledWith('/plan/pricelist/entries/p0', { headers: { 'If-Match': '4' } }));
  expect(apiClient.get).not.toHaveBeenCalledWith('/work-types', expect.anything());
});

it.each([true, false])('делает КП из разрешённых позиций и показывает неполный итог с сервера (справочник %s)', async catalog => {
  rights.push('plan.offer.edit', 'core.constructions.read');
  if (catalog) rights.push('core.catalog.read');
  serve();
  const workTypeId = catalog ? 'w2' : 'w1';
  const line = { id: 'l1', order: 1, number: '1', name: 'Строка ответа', volume: 2.5,
    unit: 'единица снимка', kind: 'work', workTypeId, lsrSection: null,
    ownMarkup: null, markup: { value: 0, level: 'offer' }, price: null, amount: null,
    marks: [{ code: 'no-price', text: 'Нет цены в прайс-листе' }] } satisfies OfferLine;
  const saved = { id: 'o1', number: 'КП-1', lines: [line], money: { total: 0, vat: null, linesTotal: 0, incomplete: true },
    vat: { mode: 'none', rate: null } } as Offer;
  vi.mocked(apiClient.post).mockResolvedValue({ data: saved });
  const onOpenOffer = vi.fn();
  mount(<PriceListOfferDialog onClose={vi.fn()} onOpenOffer={onOpenOffer} />);
  await select('Стройка', /Стройка 1/);
  if (!catalog) await screen.findByText(/Можно выбрать позицию из прайс-листа/);
  await select('Вид работы', catalog ? /Настройка/ : /Монтаж лотка/);
  click('Добавить строку');
  fireEvent.change(screen.getByRole('textbox', { name: /Объём строки 1/ }), { target: { value: '2,5' } });
  click('Создать черновик');
  await screen.findByRole('dialog', { name: 'Создан черновик КП-1' });
  expect(screen.getByText('Нет цены в прайс-листе')).toBeTruthy();
  expect(screen.getByText('Неполный итог')).toBeTruthy();
  expect(screen.getByText('единица снимка')).toBeTruthy();
  click('Открыть КП');
  expect(onOpenOffer).toHaveBeenCalledWith('o1');
  expect(apiClient.post).toHaveBeenCalledWith('/plan/offers/from-pricelist', expect.objectContaining({
    constructionId: 'c1', lines: [{ workTypeId, volume: 2.5 }],
  }));
  expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/work-types')).toBe(catalog);
});
