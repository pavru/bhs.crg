// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import type { InvoiceListItem, InvoiceView } from '@/shared/api/invoices';
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

function filterOf(favorites: string[], setOn = vi.fn()): SiteFilterValue {
  const state = filterState(favorites, true, new Set(LIVE));
  return {
    status: 'ready', favorites: state.favorites, on: true, active: state.active, sites: [],
    visible: sites => isVisible(sites, state), setOn, setFavorite: vi.fn(), setFavorites: vi.fn(),
    consumers: 0, register: () => () => {},
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
      expect(hiddenBySite(sites, { active: state.active, visible: s => isVisible(s, state) })).toBe(!c.visible);
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

/** Хук на настоящем контексте фильтра и тостах; наружу — что он вернул. */
function mount(favorites: string[], cuts: boolean, selected: string | null = null, query = '') {
  const got: { current: ReturnType<typeof useRailSites> | null } = { current: null };
  function Probe() {
    got.current = useRailSites({
      cuts, list: LIST, selected, matches: i => !query || (i.number ?? '').includes(query),
    });
    return null;
  }
  render(
    <ToastProvider>
      <SiteFilterContext.Provider value={filterOf(favorites)}><Probe /></SiteFilterContext.Provider>
    </ToastProvider>,
  );
  return got;
}

describe('рейл под фильтром', () => {
  it('скрыт счёт только неизбранных строек; неразнесённый и смешанный видны', () => {
    const rail = mount(['a'], true, 'i2').current!;
    expect(LIST.filter(i => !rail.hides(i)).map(i => i.id)).toEqual(['i1', 'i3', 'i4']);
    expect(rail.hidden).toBe(1);
    expect(rail.openHidden).toBe(true);
  });

  it('под очередью не урезает ничего', () => {
    const rail = mount(['a'], false, 'i2').current!;
    expect(LIST.some(i => rail.hides(i))).toBe(false);
    expect(rail.openHidden).toBe(false);
  });

  it('поиск считает, сколько нашёл бы среди скрытых', () => {
    expect(mount(['a'], true, null, 'Кабель').current!.foundHidden).toBe(1);
  });

  it('тост — когда своя запись увела счёт из рейла, и только тогда', async () => {
    const rail = mount(['a'], true);
    const saved = (before: InvoiceView | undefined, after: InvoiceView) =>
      act(async () => { rail.current!.onSaved(before, after); });

    await saved(view([['a']]), view([['a']]));      // осталась избранной
    await saved(undefined, view([['b']]));          // прежнего вида нет — не своя правка открытого
    await saved(view([['b']]), view([['b', 'c']]));  // и была скрыта
    expect(screen.queryByText(/Сохранено\. В списке счёт скрыт/)).toBeNull();

    await saved(view([[]]), view([['b']]));
    expect(screen.getByText(/Сохранено\. В списке счёт скрыт: он разнесён на стройки не из избранного/)).toBeTruthy();
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
