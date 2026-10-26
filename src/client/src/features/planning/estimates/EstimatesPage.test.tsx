// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { Estimate } from '@/shared/api/planningEstimates';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { EstimatesPage } from './EstimatesPage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

/**
 * Общий список смет (plan-09, задача 2.1, ревью PR #1648): пока «Избранные стройки» не известны, смет не
 * показываем вовсе — ни пустой сетки «ничего не подходит», ни «скрыто N» на отборе.
 */

function estimate(id: string, constructionId: string, name: string): Estimate {
  return {
    id, constructionId, sectionId: null, name, kind: 'main', version: '1', currentRevisionId: null,
    currentRevisionNumber: null, revisions: [],
  };
}

const SITES = [{ id: 'c1', name: 'Корпус 1' }, { id: 'c2', name: 'Корпус 2' }];

function filter(status: SiteFilterValue['status'], favorites: string[] = []): SiteFilterValue {
  const set = new Set(favorites);
  const active = status === 'ready' && set.size > 0;
  return {
    status, favorites: set, on: true, active, sites: SITES,
    visible: sites => !active || (typeof sites === 'string' && set.has(sites)),
    setOn: () => {}, setFavorite: async () => {}, setFavorites: async () => {},
  };
}

function mount(value: SiteFilterValue) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <SiteFilterContext.Provider value={value}>
        <MemoryRouter initialEntries={['/estimates']}>
          {/* Как в App.tsx: страница — под `estimates/*`, её маршруты относительные. */}
          <Routes><Route path="/estimates/*" element={<EstimatesPage />} /></Routes>
        </MemoryRouter>
      </SiteFilterContext.Provider>
    </QueryClientProvider>,
  );
  return qc;
}

beforeEach(() => {
  window.matchMedia = vi.fn().mockReturnValue({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/estimates')
      return { data: { items: [estimate('e1', 'c1', 'ЭОМ'), estimate('e2', 'c2', 'ВН')] } } as never;
    if (url === '/constructions/directory') return { data: SITES } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.mocked(apiClient.get).mockReset(); });

describe('общий список смет', () => {
  it('пока избранное грузится — «Читаем сметы…», а не «ничего не подходит» и не «скрыто N»', async () => {
    const qc = mount(filter('loading'));
    // Сметы ПРИШЛИ (запрос завершён и отрисован), а фильтр ещё нет: списка нет вовсе.
    await vi.waitFor(() => expect(qc.getQueryState(['planning', 'estimates', 'all'])?.status).toBe('success'));
    await act(async () => {});
    expect(screen.getByText('Читаем сметы…')).toBeTruthy();
    expect(screen.queryByText('Отбор ничего не нашёл')).toBeNull();
    expect(screen.queryByText(/скрыто/)).toBeNull();
  });

  it('фильтр известен — сметы по стройкам, не избранные урезаны', async () => {
    mount(filter('ready', ['c1']));
    expect(await screen.findByText('ЭОМ')).toBeTruthy();
    expect(screen.getByText('Корпус 1')).toBeTruthy();
    expect(screen.queryByText('ВН')).toBeNull();
    expect(screen.queryByText('Корпус 2')).toBeNull();
  });
});
