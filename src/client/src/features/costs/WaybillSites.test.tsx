// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { useWaybillState, waybillKey, type WaybillList, type WaybillView } from '@/shared/api/waybills';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { SiteFilterNote } from '@/shared/siteFilter/SiteFilterNotes';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { appliedHidden, useWaybillSites, viewHidden } from './useWaybillSites';

/**
 * Список накладных под «Избранными стройками» (issue #1222): отбирает сервер, клиент решает, ЧТО
 * спросить, КОГДА и что сказать. Чем ломается: первый запрос уходит до настроек и приносит «всё»;
 * очередь «Сопоставить» урезается и прячет работу за звездой; прежний список, удержанный на время
 * переключения, выдаёт себя за ответ (ревью PR #1335).
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));

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
const waybill = (constructionId: string | null, constructionLost = false) =>
  ({ id: 'w1', constructionId, constructionLost }) as WaybillView;

interface Mount { unmatched?: boolean; selected?: string | null; open?: WaybillView; writer?: () => null }

function mount(filter: SiteFilterValue, { unmatched = false, selected = null, open, writer }: Mount = {}) {
  const got: { current: ReturnType<typeof useWaybillSites> | null } = { current: null };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  function Probe({ u }: { u: boolean }) {
    got.current = useWaybillSites({ unmatched: u, selected, open });
    return null;
  }
  const Writer = writer ?? (() => null);
  const tree = (f: SiteFilterValue, u: boolean) => (
    <QueryClientProvider client={qc}>
      <ToastProvider>
        <SiteFilterContext.Provider value={f}><Probe u={u} /><Writer /></SiteFilterContext.Provider>
      </ToastProvider>
    </QueryClientProvider>
  );
  const view = render(tree(filter, unmatched));
  return Object.assign(got, { qc, rerender: (f: SiteFilterValue, u = unmatched) => view.rerender(tree(f, u)) });
}

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockResolvedValue(answer({ hidden: 0 }));
});
afterEach(cleanup);

describe('запрос списка накладных под фильтром', () => {
  it('не уходит, пока фильтр не известен, — и уходит с перечнем, когда известен', async () => {
    const page = mount(filterOf(['b', 'a'], 'loading'));
    await new Promise(r => setTimeout(r, 20));
    // Первым запросом без перечня пришло бы «всё», и список мигнул бы полным перед урезанным.
    expect(apiClient.get).not.toHaveBeenCalled();

    page.rerender(filterOf(['b', 'a']));
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    expect(asked()).toEqual([{ sites: 'a,b' }]);
  });

  it('фильтр выключен или избранное пусто после пересечения — перечня нет', async () => {
    const page = mount(filterOf(['a'], 'ready', false));
    await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(1));
    page.rerender(filterOf(['x']));
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
    mount(filterOf(['a'], 'loading', true, register), { unmatched: true });
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

describe('сколько скрыто — только у ответа на текущий перечень', () => {
  const list = (hidden: number | null) => ({ items: [], more: false, hidden });

  it('ответ на перечень — его число', () => expect(appliedHidden(['a'], list(4), false)).toBe(4));

  it('прежний список на время переключения — числа нет', () => {
    // «Все» после пустого «Избранного»: прежний пустой список иначе сказал бы «Накладных пока нет».
    expect(appliedHidden(['a'], list(4), true)).toBeNull();
    expect(appliedHidden(null, list(4), false)).toBeNull();
  });

  it('сервер не применил отбор (ни одной живой стройки в перечне) — числа нет, а не «скрыто: 0»', () => {
    expect(appliedHidden(['a'], list(null), false)).toBeNull();
    expect(appliedHidden(['a'], list(0), false)).toBe(0);
  });
});

describe('открытая накладная под фильтром', () => {
  /** «Нет в списке» — когда пришёл ответ на перечень: до него отбор не применён. */
  async function openHiddenOf(filter: SiteFilterValue, open: WaybillView, unmatched = false) {
    const page = mount(filter, { open, unmatched });
    await waitFor(() => expect(page.current!.waybills.isFetched || !page.current!.waybills.isEnabled).toBe(true));
    const result = page.current!.openHidden;
    cleanup();
    return result;
  }

  it('накладная неизбранной стройки — «нет в списке»; избранной, без стройки и удалённой — нет', async () => {
    expect(await openHiddenOf(filterOf(['a']), waybill('b'))).toBe(true);
    expect(await openHiddenOf(filterOf(['a']), waybill('a'))).toBe(false);
    expect(await openHiddenOf(filterOf(['a']), waybill(null))).toBe(false);
    expect(await openHiddenOf(filterOf(['a']), waybill('x', true))).toBe(false);
  });

  it('решает знание сервера: стройка, которой нет в справочнике клиента, но живая, — скрыта', async () => {
    // Завели после загрузки страницы: клиент счёл бы её удалённой, а сервер накладную скрыл (ревью PR #1335).
    expect(await openHiddenOf(filterOf(['a']), waybill('z'))).toBe(true);
  });

  it('сервер отбор не применил (ни одной живой стройки в перечне) — не скрыто ничего', async () => {
    vi.mocked(apiClient.get).mockResolvedValue(answer({ hidden: null }));
    expect(await openHiddenOf(filterOf(['a']), waybill('b'))).toBe(false);
  });

  it('под очередью и с выключенным фильтром — ничего не скрыто', async () => {
    expect(await openHiddenOf(filterOf(['a']), waybill('b'), true)).toBe(false);
    expect(await openHiddenOf(filterOf(['a'], 'ready', false), waybill('b'))).toBe(false);
  });

  it('правило вида накладной', () => {
    const favorites = new Set(['a']);
    expect(viewHidden(waybill('b'), favorites)).toBe(true);
    expect(viewHidden(waybill('b', true), favorites)).toBe(false);
    expect(viewHidden(waybill('a'), favorites)).toBe(false);
    expect(viewHidden(waybill(null), favorites)).toBe(false);
  });
});

describe('тост своей записи', () => {
  function writerOf(after: WaybillView) {
    let write!: () => Promise<unknown>;
    function Writer() {
      const mutation = useWaybillState();
      write = () => mutation.mutateAsync({ id: 'w1', seen: '1', to: 'posted' });
      return null;
    }
    vi.mocked(apiClient.post).mockResolvedValue({ data: after });
    return { Writer, write: () => write() };
  }

  it('своя запись увела открытую накладную на неизбранную стройку — тост с «Показать все»', async () => {
    const { Writer, write } = writerOf(waybill('b'));
    const page = mount(filterOf(['a']), { selected: 'w1', writer: Writer });
    await waitFor(() => expect(page.current!.hidden).toBe(0));
    page.qc.setQueryData(waybillKey('w1'), waybill('a'));
    await act(async () => { await write(); });
    expect(screen.getAllByText(/Сохранено\. В списке накладная скрыта/).length).toBeGreaterThan(0);
  });

  it('накладная и до записи была скрыта, или открыта другая — тоста нет', async () => {
    const { Writer, write } = writerOf(waybill('b'));
    const page = mount(filterOf(['a']), { selected: 'w1', writer: Writer });
    // Ответ на перечень пришёл: без него отбор не применён, и тоста не было бы ни при каком условии.
    await waitFor(() => expect(page.current!.hidden).toBe(0));
    page.qc.setQueryData(waybillKey('w1'), waybill('c'));
    await act(async () => { await write(); });
    cleanup();
    const other = writerOf(waybill('b'));
    const moved = mount(filterOf(['a']), { selected: 'w2', writer: other.Writer });
    await waitFor(() => expect(moved.current!.hidden).toBe(0));
    moved.qc.setQueryData(waybillKey('w1'), waybill('a'));
    await act(async () => { await other.write(); });
    expect(screen.queryByText(/Сохранено\. В списке накладная скрыта/)).toBeNull();
  });
});

function showNote(hidden: number, partial: boolean) {
  render(
    <SiteFilterContext.Provider value={filterOf(['a'])}><SiteFilterNote hidden={hidden} partial={partial} /></SiteFilterContext.Provider>,
  );
  return screen.getByTestId('site-filter-note').textContent;
}

describe('свидетельство у списка накладных', () => {
  it('без поиска — число скрытых, и под пределом тоже: оно по всему отбору', () => {
    expect(showNote(4, false)).toContain('скрыто: 4');
  });

  it('под поиском — «часть скрыта», без числа: поиск скрытых не видит', () => {
    const text = showNote(4, true);
    expect(text).toContain('часть скрыта');
    expect(text).not.toContain('4');
  });

  it('под поиском, но скрытых нет — так и сказано', () => expect(showNote(0, true)).toContain('скрыто: 0'));
});
