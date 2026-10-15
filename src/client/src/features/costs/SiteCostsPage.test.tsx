// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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

const SITES = [{ id: 'a', name: 'Комарова 36' }, { id: 'b', name: 'Ленина 5' }, { id: 'c', name: 'Склад' }];

function filterOf(favorites: string[]): SiteFilterValue {
  const state = filterState(favorites, true, new Set(SITES.map(s => s.id)));
  return {
    status: 'ready', favorites: state.favorites, on: true, active: state.active, sites: SITES,
    visible: s => isVisible(s, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
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

function mount(filter: SiteFilterValue) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SiteFilterContext.Provider value={filter}>
        <MemoryRouter initialEntries={['/site-costs?from=2026-09&to=2026-09']}><SiteCostsPage /></MemoryRouter>
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

describe('отчёт всех строек под фильтром', () => {
  it('неизбранные — одной строкой без числа счетов; итог тот же', async () => {
    mount(filterOf(['a']));
    const folded = await screen.findByTestId('site-costs-folded');
    expect(folded.textContent).toContain('Скрыто фильтром: 2 стройки');
    expect(folded.querySelectorAll('td')[0].textContent).toBe('');
    expect(screen.queryByText('Ленина 5')).toBeNull();
    expect(screen.getByRole('row', { name: /Итого затраты/ }).textContent).toContain('123');
  });

  it('раскрытое — только для этого отчёта: другой период приходит снова свёрнутым', async () => {
    mount(filterOf(['a']));
    fireEvent.click(await screen.findByRole('button', { name: 'Показать построчно' }));
    await screen.findByText('Ленина 5');
    expect(screen.queryByTestId('site-costs-folded')).toBeNull();

    fireEvent.change(screen.getByLabelText('по'), { target: { value: '2026-10' } });
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([, c]) =>
      (c as { params?: { to?: string } } | undefined)?.params?.to === '2026-10')).toBe(true));
    await screen.findByTestId('site-costs-folded');
    expect(screen.queryByText('Ленина 5')).toBeNull();
  });
});
