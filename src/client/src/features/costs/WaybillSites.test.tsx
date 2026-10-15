// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { useWaybills, type WaybillList } from '@/shared/api/waybills';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { SiteFilterNote } from '@/shared/siteFilter/SiteFilterNotes';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { useWaybillSites } from './useWaybillSites';
import { WaybillSitesEmpty } from './WaybillSitesEmpty';

/**
 * Список накладных под «Избранными стройками» (issue #1222): отбирает сервер, клиент решает, ЧТО
 * спросить и КОГДА. Чем ломается: первый запрос уходит до настроек и приносит «всё» — список мигает
 * полным; очередь «Сопоставить» урезается и прячет работу за звездой.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

const LIVE = ['a', 'b', 'c'];

function filterOf(favorites: string[], status: SiteFilterValue['status'] = 'ready', on = true,
  register = () => () => {}): SiteFilterValue {
  const state = filterState(favorites, on, new Set(LIVE));
  return {
    status, favorites: state.favorites, on, active: status === 'ready' && state.active, sites: [],
    visible: sites => isVisible(sites, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register,
  };
}

const answer = (data: Partial<WaybillList> = {}) => ({ data: { items: [], more: false, ...data } });
const asked = () => vi.mocked(apiClient.get).mock.calls.map(([, config]) => (config as { params: object }).params);

function Probe({ unmatched }: { unmatched: boolean }) {
  const sites = useWaybillSites(unmatched);
  useWaybills(unmatched, sites.sites, sites.ready);
  return null;
}

function mount(filter: SiteFilterValue, unmatched = false) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const tree = (f: SiteFilterValue, u: boolean) => (
    <QueryClientProvider client={qc}>
      <SiteFilterContext.Provider value={f}><Probe unmatched={u} /></SiteFilterContext.Provider>
    </QueryClientProvider>
  );
  const view = render(tree(filter, unmatched));
  return (f: SiteFilterValue, u = unmatched) => view.rerender(tree(f, u));
}

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockResolvedValue(answer({ hidden: 0 }));
});
afterEach(cleanup);

describe('запрос списка накладных под фильтром', () => {
  it('не уходит, пока фильтр не известен, — и уходит с перечнем, когда известен', async () => {
    const rerender = mount(filterOf(['b', 'a'], 'loading'));
    await new Promise(r => setTimeout(r, 20));
    // Первым запросом без перечня пришло бы «всё», и список мигнул бы полным перед урезанным.
    expect(apiClient.get).not.toHaveBeenCalled();

    rerender(filterOf(['b', 'a']));
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    expect(asked()).toEqual([{ sites: 'a,b' }]);
  });

  it('фильтр выключен или избранное пусто после пересечения — перечня нет', async () => {
    const rerender = mount(filterOf(['a'], 'ready', false));
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    rerender(filterOf(['x']));
    await new Promise(r => setTimeout(r, 20));
    // Тот же ключ «без перечня» — второго запроса нет, и ни один не несёт sites.
    expect(asked()).toEqual([{}]);
  });

  it('удалённая стройка в перечень не уходит', async () => {
    mount(filterOf(['x', 'c']));
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    expect(asked()).toEqual([{ sites: 'c' }]);
  });

  it('очередь «Сопоставить» не урезается, не ждёт фильтра и экран не регистрирует', async () => {
    const register = vi.fn(() => () => {});
    mount(filterOf(['a'], 'loading', true, register), true);
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    expect(asked()).toEqual([{ unmatched: true }]);
    expect(register).not.toHaveBeenCalled();
  });

  it('общий список экран регистрирует — шапка горит над урезанным', () => {
    const register = vi.fn(() => () => {});
    mount(filterOf(['a'], 'ready', true, register));
    expect(register).toHaveBeenCalledTimes(1);
  });
});

function showNote(hidden: number, partial: boolean) {
  render(
    <SiteFilterContext.Provider value={filterOf(['a'])}><SiteFilterNote hidden={hidden} partial={partial} /></SiteFilterContext.Provider>,
  );
  return screen.getByTestId('site-filter-note').textContent;
}

describe('свидетельство у списка накладных', () => {
  it('без предела — число скрытых', () => expect(showNote(4, false)).toContain('скрыто: 4'));

  it('предел сработал — «часть скрыта», без числа', () => {
    const text = showNote(4, true);
    expect(text).toContain('часть скрыта');
    expect(text).not.toContain('4');
  });

  it('предел сработал, но скрытых нет — так и сказано', () => expect(showNote(0, true)).toContain('скрыто: 0'));

  it('пустой список: скрытые названы числом, выход — «Показать все стройки»', () => {
    const onShowAll = vi.fn();
    render(<WaybillSitesEmpty searching={false} hidden={3} onShowAll={onShowAll} />);
    expect(screen.getByText('По избранным стройкам накладных нет. Скрыто фильтром: 3.')).toBeTruthy();
    fireEvent.click(screen.getByText('Показать все стройки'));
    expect(onShowAll).toHaveBeenCalled();
  });

  it('поиск: «среди избранных не найдено», без числа — скрытых сервер не прислал', () => {
    render(<WaybillSitesEmpty searching hidden={3} onShowAll={() => {}} />);
    expect(screen.getByText('Среди избранных строек не найдено.')).toBeTruthy();
    expect(screen.getByText('Искать по всем стройкам')).toBeTruthy();
    expect(screen.getByTestId('waybill-list-sites-empty').textContent).not.toContain('3');
  });
});
