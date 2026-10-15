// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { TableColumn, TableDeclaration } from '@/shared/api/tables';
import type { FilterGroup } from '@/shared/api/types';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { TablePage } from './TablePage';
import { favoriteNames, namesakes, sitesChipState, sitesColumn, withSitesChip } from './tableSites';
import { DEFAULT_VIEW, type TableView } from './tableViewState';

/**
 * Чип «Объект: избранные стройки» (issue #1223). Чем ломается: реестр, открытый под включённым фильтром,
 * молча показывает часть счетов — отбор, которого человек не ставил, а «Сумма» под ним становится долей.
 * Сторож — первый тест: открытие под фильтром не добавляет отбора само, ни в запрос, ни в адрес.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

const SITES = [{ id: 'a', name: 'Комарова 36' }, { id: 'b', name: 'Ленина 5' }, { id: 'c', name: 'Склад' }];

function filterOf(favorites: string[], on = true, sites = SITES): SiteFilterValue {
  const state = filterState(favorites, on, new Set(sites.map(s => s.id)));
  return {
    status: 'ready', favorites: state.favorites, on, active: state.active, sites,
    visible: s => isVisible(s, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
  };
}

const column = (key: string, patch: Partial<TableColumn> = {}): TableColumn => ({
  key, label: key, kind: 'text', operators: ['eq', 'in'], system: true, unavailable: null, reason: null,
  dependsOnFilter: false, onDemand: false, note: null, options: null, requires: null, ...patch,
});

const COLUMNS = [column('Номер'), column('ОбъектыРазноски', { label: 'Объект', kind: 'list', sites: true })];
const DECLARATION: TableDeclaration = {
  address: 'costs.invoices', title: 'Счета', grain: 'счёт', boundary: '', columns: COLUMNS, state: null, views: [],
};

/** Отборы, с которыми уходили запросы строк: `undefined` — без отбора. */
const rowFilters = () => vi.mocked(apiClient.get).mock.calls
  .filter(([url]) => url === '/tables/costs.invoices')
  .map(([, config]) => (config as { params: { filter?: string } }).params.filter);

function mount(filter: SiteFilterValue) {
  window.history.pushState(null, '', '/tables/costs.invoices');
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ToastProvider>
        <SiteFilterContext.Provider value={filter}>
          <BrowserRouter>
            <Routes><Route path="/tables/:address/:view?" element={<TablePage />} /></Routes>
          </BrowserRouter>
        </SiteFilterContext.Provider>
      </ToastProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url.endsWith('/columns')) return { data: DECLARATION };
    if (url.endsWith('/views') || url.endsWith('/shortcuts')) return { data: [] };
    if (url === '/account/access') return { data: { permissions: [], modules: [] } };
    return { data: { ...DECLARATION, rows: [], count: 0, offset: 0, limit: 50, totals: null, keys: null } };
  });
});
afterEach(cleanup);

describe('таблица со стройками под включённым фильтром', () => {
  it('открывается без отбора — фильтр только предлагает чип', async () => {
    mount(filterOf(['a']));
    const chip = await screen.findByRole('button', { name: /Объект: избранные стройки/ });
    await waitFor(() => expect(rowFilters().length).toBeGreaterThan(0));

    expect(rowFilters().every(f => f === undefined)).toBe(true);
    expect(window.location.hash).toBe('');
    expect(chip.getAttribute('aria-pressed')).toBe('false');
  });

  it('нажатие ставит обычное условие «из списка» по названиям избранных', async () => {
    mount(filterOf(['b', 'a']));
    fireEvent.click(await screen.findByRole('button', { name: /Объект: избранные стройки/ }));

    await waitFor(() => expect(rowFilters().at(-1)).toBeDefined());
    expect((JSON.parse(rowFilters().at(-1)!) as FilterGroup).children).toEqual([
      { type: 'condition', column: 'ОбъектыРазноски', op: 'in', values: ['Комарова 36', 'Ленина 5'] },
    ]);
    expect(window.location.hash).toContain('filter=');
    expect(screen.getByRole('button', { name: /Объект: избранные стройки/ }).getAttribute('aria-pressed')).toBe('true');
  });

  it('фильтр выключен или избранное пусто — чипа нет', async () => {
    for (const filter of [filterOf(['a'], false), filterOf([])]) {
      mount(filter);
      await waitFor(() => expect(rowFilters().length).toBeGreaterThan(0));
      expect(screen.queryByRole('button', { name: /избранные стройки/ })).toBeNull();
      cleanup();
      vi.mocked(apiClient.get).mockClear();
    }
  });
});

describe('правила чипа', () => {
  const view = (filter: TableView['filter'] = null): TableView => ({ ...DEFAULT_VIEW, filter });

  it('колонка — со стройками, открытая и с «из списка»; иначе чипа нет', () => {
    expect(sitesColumn(COLUMNS)?.key).toBe('ОбъектыРазноски');
    expect(sitesColumn([column('Объект', { sites: true, unavailable: 'no-right' })])).toBeUndefined();
    expect(sitesColumn([column('Объект', { sites: true, operators: ['eq'] })])).toBeUndefined();
    expect(sitesColumn([column('Объект')])).toBeUndefined();
  });

  it('одноимённая неизбранная стройка названа: отбор по названию покажет и её', () => {
    const sites = [...SITES, { id: 'd', name: 'Комарова 36' }];
    expect(favoriteNames(sites, new Set(['a', 'd']))).toEqual(['Комарова 36']);
    expect(namesakes(sites, new Set(['a']))).toEqual(['Комарова 36']);
    expect(namesakes(sites, new Set(['a', 'd']))).toEqual([]);
  });

  it('снятие убирает своё условие, а поставленное человеком по той же колонке оставляет', () => {
    const own = { type: 'condition' as const, column: 'ОбъектыРазноски', op: 'eq' as const, value: 'Склад' };
    const names = ['Комарова 36'];
    const on = withSitesChip(view({ type: 'group', logic: 'and', children: [own] }), ['Номер'], 'ОбъектыРазноски', names);
    expect((on.filter as FilterGroup).children).toHaveLength(2);
    expect(on.columns).toContain('ОбъектыРазноски');
    expect(sitesChipState(on, 'ОбъектыРазноски', names)).toBe('on');

    const off = withSitesChip(on, ['Номер'], 'ОбъектыРазноски', names);
    expect((off.filter as FilterGroup).children).toEqual([own]);
  });

  it('сложный и неразобранный отбор чипом не дополнить', () => {
    const complex: TableView['filter'] = {
      type: 'group', logic: 'or', children: [
        { type: 'condition', column: 'Номер', op: 'eq', value: '1' },
        { type: 'condition', column: 'Номер', op: 'eq', value: '2' },
      ],
    };
    expect(sitesChipState(view(complex), 'ОбъектыРазноски', ['Комарова 36'])).toBe('complex');
    expect(sitesChipState({ filter: null, brokenFilter: '{' }, 'ОбъектыРазноски', ['Комарова 36'])).toBe('broken');
  });
});
