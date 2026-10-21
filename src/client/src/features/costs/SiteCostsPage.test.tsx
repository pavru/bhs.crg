// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { CostLine, SiteCosts } from '@/shared/api/siteCosts';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { SiteCostsPage } from './SiteCostsPage';

/**
 * «Затраты по стройке» под «Избранными стройками» (issue #1223): неизбранные — одной строкой, раскрытие —
 * на месте и только для ЭТОГО отчёта. Чем ломается: раскрытое однажды остаётся раскрытым на другом периоде,
 * и под горящим фильтром отчёт показывает все стройки (ревью PR #1339).
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));
beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});

const SITES = [{ id: 'a', name: 'Комарова 36' }, { id: 'b', name: 'Ленина 5' }, { id: 'c', name: 'Склад' }];

function filterOf(favorites: string[]): SiteFilterValue {
  const state = filterState(favorites, true, new Set(SITES.map(s => s.id)));
  return {
    status: 'ready', favorites: state.favorites, on: true, active: state.active, sites: SITES,
    visible: s => isVisible(s, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
  };
}

const line = (id: string, name: string, amount: number): CostLine => ({ id, name, invoices: 1, amount, linked: true });

/** Ответ отчёта на тот период, о котором спросили. */
function report(from: string, to: string): SiteCosts {
  const zero = { invoices: 0, amount: 0 };
  return {
    site: null, from, to, withVat: true, months: [from], articles: [], lost: null, unallocated: null,
    sites: [line('a', 'Комарова 36', 100), line('b', 'Ленина 5', 20), line('c', 'Склад', 3)],
    suppliers: [], total: { invoices: 3, amount: 123 }, unmatched: null, payable: zero, vatUnknown: null,
    payableVatUnknown: null,
  };
}

function mount(filter: SiteFilterValue, query = 'from=2026-09&to=2026-09') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SiteFilterContext.Provider value={filter}>
        <MemoryRouter initialEntries={[`/site-costs?${query}`]}><SiteCostsPage /></MemoryRouter>
      </SiteFilterContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async (url: string, config?: unknown) => {
    if (url === '/costs/constructions') return { data: SITES.map(s => ({ ...s, sections: [] })) };
    const params = (config as { params: { from: string; to: string } }).params;
    return { data: report(params.from, params.to) };
  });
});
afterEach(cleanup);

/** Сегмент месяца границы периода. */
const segment = (side: 'с' | 'по') =>
  within(screen.getByRole('group', { name: `Учётный период, ${side}` })).getByRole('textbox', { name: 'месяц' });
function month(side: 'с' | 'по', mm: string) {
  fireEvent.focus(segment(side));
  fireEvent.change(segment(side), { target: { value: mm } });
}
/** Периоды, о которых спросили сервер, — «с|по». */
const asked = () => vi.mocked(apiClient.get).mock.calls
  .map(([, c]) => (c as { params?: { from?: string; to?: string } } | undefined)?.params)
  .filter(p => p?.from).map(p => `${p!.from}|${p!.to}`);

describe('отчёт всех строек под фильтром', () => {
  it('неизбранные — одной строкой без числа счетов; итог тот же', async () => {
    mount(filterOf(['a']));
    const folded = await screen.findByTestId('site-costs-folded');
    expect(folded.textContent).toContain('Скрыто фильтром: 2 стройки');
    expect(folded.closest('tr')!.querySelectorAll('td')[0].textContent).toBe('');
    expect(screen.queryByText('Ленина 5')).toBeNull();
    expect(screen.getByRole('row', { name: /Итого затраты/ }).textContent).toContain('123');
  });

  it('раскрытое — только для этого отчёта: другой период приходит снова свёрнутым', async () => {
    mount(filterOf(['a']));
    fireEvent.click(await screen.findByRole('button', { name: 'Показать построчно' }));
    await screen.findByText('Ленина 5');
    expect(screen.queryByTestId('site-costs-folded')).toBeNull();

    month('по', '10');
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([, c]) =>
      (c as { params?: { to?: string } } | undefined)?.params?.to === '2026-10')).toBe(true));
    await screen.findByTestId('site-costs-folded');
    expect(screen.queryByText('Ленина 5')).toBeNull();
  });
});

/**
 * Учётный период (issue #1466): поле месяца маской языка, а не нативное. В адрес и на сервер — ГГГГ-ММ:
 * ГГГГ-ММ-01 сервер отвергает «месяц назван не так», «по» раньше «с» — «период задан наоборот».
 */
describe('учётный период', () => {
  it('месяц уходит на сервер как ГГГГ-ММ', async () => {
    mount(filterOf([]));
    await screen.findByRole('row', { name: /Итого затраты/ });
    expect(screen.getByRole('group', { name: 'Учётный период, с' }).querySelector('input[type="month"]')).toBeNull();
    month('по', '11');
    await waitFor(() => expect(asked()).toContain('2026-09|2026-11'));
    expect(asked().every(p => /^\d{4}-\d{2}\|\d{4}-\d{2}$/.test(p))).toBe(true);
  });

  it('«по» раньше «с» на сервер не уходит; уход из поля поправляет «с»', async () => {
    mount(filterOf([]), 'from=2026-09&to=2026-10');
    await screen.findByRole('row', { name: /Итого затраты/ });
    month('по', '08');
    expect(segment('по')).toHaveProperty('value', '08');
    fireEvent.blur(segment('по'), { relatedTarget: screen.getByRole('button', { name: 'с НДС' }) });
    await waitFor(() => expect(asked()).toContain('2026-08|2026-08'));
    expect(asked()).not.toContain('2026-09|2026-08');
    expect(segment('с')).toHaveProperty('value', '08');
  });

  it('поправка при уходе на «без НДС» не теряется щелчком по ней', async () => {
    mount(filterOf([]), 'from=2026-09&to=2026-10');
    await screen.findByRole('row', { name: /Итого затраты/ });
    month('по', '08');
    const without = screen.getByRole('button', { name: 'без НДС' });
    // Одним тактом, без отрисовки между: в браузере щелчок приходит раньше, чем адрес перерисован.
    act(() => {
      fireEvent.blur(segment('по'), { relatedTarget: without });
      fireEvent.click(without);
    });
    await waitFor(() => expect(without).toHaveProperty('ariaPressed', 'true'));
    expect(asked().at(-1)).toBe('2026-08|2026-08');
    expect(segment('с')).toHaveProperty('value', '08');
  });

  it('стёртую границу на сервер не отдаёт: отчёт без периода не строится', async () => {
    mount(filterOf([]));
    await screen.findByRole('row', { name: /Итого затраты/ });
    const before = asked().length;
    month('с', '');
    fireEvent.blur(segment('с'), { relatedTarget: screen.getByRole('button', { name: 'с НДС' }) });
    expect(asked().slice(before).filter(p => p !== '2026-09|2026-09')).toEqual([]);
    expect(screen.queryByRole('button', { name: 'Очистить дату' })).toBeNull();
  });
});
