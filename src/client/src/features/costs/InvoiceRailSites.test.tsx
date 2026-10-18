// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { QK, useInvoiceWrite, type InvoiceListItem, type InvoiceView } from '@/shared/api/invoices';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { filterState, isVisible } from '@/shared/siteFilter/rule';
import cases from '@/shared/siteFilter/siteFilterCases.json';
import { RailSitesEmpty } from './InvoiceRailSites';
import { useRailSites } from './useRailSites';
import { hiddenBySite, viewSites } from './invoiceSites';

/**
 * Рейл счетов под фильтром «Избранные стройки» (issue #1221): отбор правилом из #1218, тексты пустого
 * рейла и тост после СВОЕЙ записи, уводящей счёт из списка.
 */

afterEach(cleanup);

const LIVE = ['a', 'b', 'c'];

function filterOf(
  favorites: string[], status: SiteFilterValue['status'] = 'ready',
): SiteFilterValue {
  const state = filterState(favorites, true, new Set(LIVE));
  return {
    status, favorites: state.favorites, on: true, active: status === 'ready' && state.active, sites: [],
    visible: sites => isVisible(sites, state), setOn: vi.fn(), setFavorite: vi.fn(), setFavorites: vi.fn(),
  };
}

describe('отбор рейла — общий файл случаев (#1218)', () => {
  // Положительный якорь: пустой файл прошёл бы цикл, ничего не проверив.
  it('файл случаев прочитан', () => expect(cases.cases.length).toBeGreaterThanOrEqual(10));

  for (const c of cases.cases) {
    it(c.name, () => {
      // Элемент списка несёт стройки массивом: «без стройки» — пустой массив, пропуски выброшены.
      const sites = (Array.isArray(c.sites) ? c.sites : [c.sites]).filter((s): s is string => !!s);
      const state = filterState(c.favorites, c.on, new Set(cases.live));
      expect(hiddenBySite(sites, { visible: s => isVisible(s, state) })).toBe(!c.visible);
    });
  }
});

const part = (constructionId: string | null) => ({ constructionId }) as never;
const view = (lines: (string | null)[][], document: (string | null)[] = []) => ({
  id: 'i1',
  lines: lines.map(sites => ({ allocation: { parts: sites.map(part) } })),
  allocation: { document: { parts: document.map(part) } },
}) as unknown as InvoiceView;

describe('стройки открытого счёта', () => {
  it('по строкам и по частям счёта целиком, каждая один раз, без статей', () => {
    expect(viewSites(view([['a', 'b', 'a'], [null]], ['c']))).toEqual(['a', 'b', 'c']);
    expect(viewSites(view([]))).toEqual([]);
  });
});

const item = (id: string, constructionIds: string[], number = id) =>
  ({ id, number, supplierName: null, purpose: null, scanFileName: null, constructionIds }) as unknown as InvoiceListItem;
const LIST = [item('i1', ['a']), item('i2', ['b'], 'Кабель'), item('i3', []), item('i4', ['b', 'a'])];

interface Mount {
  selected?: string | null;
  query?: string;
  status?: SiteFilterValue['status'];
  /** Пишущий хук счёта рядом с рейлом — запись идёт настоящей мутацией через кэш. */
  writer?: () => null;
}

/** Хук на настоящем контексте фильтра, кэше запросов и тостах; наружу — что он вернул. */
function mount(favorites: string[], cuts: boolean, { selected = 'i1', query = '', status, writer }: Mount = {}) {
  const got: { current: ReturnType<typeof useRailSites> | null } = { current: null };
  const qc = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  function Probe() {
    got.current = useRailSites({
      cuts, list: LIST, selected, matches: i => !query || (i.number ?? '').includes(query),
    });
    return null;
  }
  const Writer = writer ?? (() => null);
  render(
    <QueryClientProvider client={qc}>
      <ToastProvider>
        <SiteFilterContext.Provider value={filterOf(favorites, status)}><Probe /><Writer /></SiteFilterContext.Provider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  return Object.assign(got, { qc });
}

describe('рейл под фильтром', () => {
  it('скрыт счёт только неизбранных строек; неразнесённый и смешанный видны', () => {
    const rail = mount(['a'], true, { selected: 'i2' }).current!;
    expect(LIST.filter(i => !rail.hides(i)).map(i => i.id)).toEqual(['i1', 'i3', 'i4']);
    expect(rail.hidden).toBe(1);
    expect(rail.openHidden).toBe(true);
  });

  it('под очередью не урезает ничего', () => {
    const rail = mount(['a'], false, { selected: 'i2' }).current!;
    expect(LIST.some(i => rail.hides(i))).toBe(false);
    expect(rail.openHidden).toBe(false);
  });

  it('пока фильтр не известен — общий список ждёт, очередь нет', () => {
    expect(mount(['a'], true, { status: 'loading' }).current!.pending).toBe(true);
    cleanup();
    expect(mount(['a'], false, { status: 'loading' }).current!.pending).toBe(false);
    cleanup();
    expect(mount(['a'], true).current!.pending).toBe(false);
  });

  it('поиск считает, сколько нашёл бы среди скрытых; без запроса — все скрытые', () => {
    expect(mount(['a'], true, { query: 'Кабель' }).current!.foundHidden).toBe(1);
    cleanup();
    expect(mount(['a'], true, { query: 'Нет такого' }).current!.foundHidden).toBe(0);
    cleanup();
    const rail = mount(['a'], true).current!;
    expect(rail.foundHidden).toBe(rail.hidden);
  });

  it('тост — когда своя запись увела счёт из рейла, и только тогда', async () => {
    const rail = mount(['a'], true);
    const saved = (before: InvoiceView | undefined, after: InvoiceView) =>
      act(async () => { rail.current!.onSaved(before, after); });

    await saved(view([['a']]), view([['a']]));      // осталась избранной
    await saved(undefined, view([['b']]));          // прежнего вида нет — не своя правка открытого
    await saved(view([['b']]), view([['b', 'c']]));  // и была скрыта
    await saved(view([[]]), { ...view([['b']]), id: 'i9' });  // не открытый счёт: ушли на другой
    expect(screen.queryByText(/Сохранено\. В списке счёт скрыт/)).toBeNull();

    await saved(view([[]]), view([['b']]));
    expect(screen.getByText(/Сохранено\. В списке счёт скрыт: он разнесён на стройки не из избранного/)).toBeTruthy();
  });

  it('тост приходит от настоящей записи useInvoiceWrite, без провайдера на странице', async () => {
    // Пишущий хук — СОСЕД рейла, а не потомок чего-то особого: запись видна через кэш мутаций.
    let write!: () => Promise<unknown>;
    function Writer() {
      const mutation = useInvoiceWrite(async (_: { id: string }) => view([['b']]));
      write = () => mutation.mutateAsync({ id: 'i1' });
      return null;
    }
    const rail = mount(['a'], true, { writer: Writer });
    rail.qc.setQueryData([QK, 'i1'], view([['a']]));
    await act(async () => { await write(); });
    expect(screen.getByText(/Сохранено\. В списке счёт скрыт/)).toBeTruthy();
  });

  it('под очередью тоста нет: список там не урезан', async () => {
    const rail = mount(['a'], false);
    await act(async () => { rail.current!.onSaved(view([['a']]), view([['b']])); });
    expect(screen.queryByText(/Сохранено\. В списке счёт скрыт/)).toBeNull();
  });
});

describe('пустой рейл под фильтром', () => {
  it('счета есть, но скрыты — говорит число и выход', () => {
    const onShowAll = vi.fn();
    render(<RailSitesEmpty searching={false} hidden={3} foundHidden={0} onShowAll={onShowAll} />);
    expect(screen.getByText('По избранным стройкам счетов нет. Скрыто фильтром: 3.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать все стройки' }));
    expect(onShowAll).toHaveBeenCalled();
  });

  it('поиск нашёл бы среди скрытых — «искать по всем»', () => {
    render(<RailSitesEmpty searching hidden={3} foundHidden={1} onShowAll={() => {}} />);
    expect(screen.getByText('Среди избранных строек не найдено.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Искать по всем стройкам' })).toBeTruthy();
  });

  it('скрытых нет — молчит, говорит обычное пустое состояние', () => {
    const { container } = render(<RailSitesEmpty searching={false} hidden={0} foundHidden={0} onShowAll={() => {}} />);
    expect(container.textContent).toBe('');
    cleanup();
    const searched = render(<RailSitesEmpty searching hidden={3} foundHidden={0} onShowAll={() => {}} />);
    expect(searched.container.textContent).toBe('');
  });
});
