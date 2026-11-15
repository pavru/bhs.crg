// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { BrowserRouter, useLocation } from 'react-router';
import type { TableColumn, TableDeclaration } from '@/shared/api/tables';
import { FilterChips } from '@/shared/filter/FilterChips';
import { HistoryLayersHost, resetHistoryLayers } from '@/shared/ui/historyLayers';
import { ColumnsPanel } from './ColumnsPanel';
import { TableViewHeader } from './TableViewHeader';
import { viewLookup } from './savedViews';
import { DEFAULT_VIEW, parseView, withSort } from './tableViewState';
import { useTableView } from './useTableView';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import '@/shared/ui/NavigationGuard';

vi.mock('@/shared/api/access', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/api/access')>()),
  useAccess: () => ({ data: undefined }),
}));
vi.mock('@/shared/api/tableViews', () => ({
  useSaveTableView: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteTableView: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ success: vi.fn(), apiError: vi.fn() }) }));

/**
 * Поповеры экрана таблицы — слои истории на телефоне ([UI-96]): «Назад» закрывает их, а страница и её
 * адрес остаются. Маршрутизатор — настоящий `BrowserRouter`: состояние экрана живёт в адресе браузера
 * (`useTableView` читает `window.location`), и расхождение адреса с показанным иначе не увидеть.
 */

const settle = () => act(() => new Promise(r => setTimeout(r, 30)));
const state = () => window.history.state as { idx?: number; layer?: string; depth?: number } | null;

const column = (key: string, label: string): TableColumn => ({
  key, label, kind: 'text', operators: [], system: false, unavailable: null, reason: null, dependsOnFilter: false,
  onDemand: false, note: null, options: null, requires: null,
} as TableColumn);
const COLUMNS = [column('a', 'Номер'), column('b', 'Поставщик'), column('c', 'Сумма')];
const DECL = {
  address: 't.x', title: 'Счета', grain: 'счёт', boundary: null, columns: COLUMNS, state: null,
  views: [{ code: 'ready', title: 'Реестр', columns: ['a', 'c'], sort: [], totals: [], pinned: 0, filters: [] }],
} as TableDeclaration;

/** Экран таблицы в малом: шапка с выбором представления, «Колонки» и сортировка — как у `TablePage`. */
function Screen() {
  const { pathname } = useLocation();
  const lookup = viewLookup(pathname.split('/')[3], DECL, []);
  const [view, setView] = useTableView(DEFAULT_VIEW);
  return (
    <>
      <HistoryLayersHost />
      <TableViewHeader address="t.x" decl={DECL} boundary={null} lookup={lookup} view={view} base={DEFAULT_VIEW}
        saved={[]} savedProblem={null} onChange={setView} onLeaving={() => {}} />
      <ColumnsPanel columns={COLUMNS} view={view} base={DEFAULT_VIEW} gridColumns={3} onChange={setView} />
      <button type="button" onClick={() => setView(v => withSort(v, 'a', false))}>по номеру</button>
    </>
  );
}

const ownMatchMedia = window.matchMedia;

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterAll(() => { window.matchMedia = ownMatchMedia; });

beforeEach(() => {
  resetHistoryLayers();
  // Узкий экран: «шире 768» — нет, «уже 768» — да.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: !q.includes('min-width'), addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
  window.history.replaceState({ idx: 0, key: 'base', usr: null }, '', '/tables/t.x');
});
afterEach(cleanup);

/** Слой — селектором: после измерения в jsdom Radix прячет слой, и поиск по роли его не видит. */
const layer = (name: string) => document.querySelector<HTMLElement>(`[role="dialog"][aria-label="${name}"]`);
const shownColumns = () => parseView(window.location.hash).columns;

describe('таблица на телефоне: «Назад» закрывает поповер', () => {
  it('колонка снята при открытом «Колонки» — после «Назад» снятой остаётся и в адресе, и после сортировки', async () => {
    render(<BrowserRouter><Screen /></BrowserRouter>);
    await settle();
    fireEvent.click(screen.getByRole('button', { name: 'Колонки' }));
    await settle();
    expect(layer('Колонки и итоги')).not.toBeNull();
    expect(state()?.depth).toBe(1);

    fireEvent.click(within(layer('Колонки и итоги')!).getByText('Поставщик'));
    await settle();
    expect(shownColumns()).toEqual(['a', 'c']);

    window.history.back();
    await settle();
    expect(layer('Колонки и итоги')).toBeNull();
    expect(state()?.layer).toBeUndefined();
    expect(window.location.pathname).toBe('/tables/t.x');
    expect(shownColumns()).toEqual(['a', 'c']);

    // Следующая правка считается от адреса — снятая колонка не возвращается.
    fireEvent.click(screen.getByRole('button', { name: 'по номеру' }));
    await settle();
    expect(shownColumns()).toEqual(['a', 'c']);
    expect(parseView(window.location.hash).sort).toEqual([{ column: 'a', descending: false }]);
  });

  it('выбрано другое представление — «Назад» возвращает к прежнему одним шагом', async () => {
    render(<BrowserRouter><Screen /></BrowserRouter>);
    await settle();
    fireEvent.click(screen.getByRole('button', { name: /Счета/ }));
    await settle();
    expect(layer('Представления таблицы')).not.toBeNull();
    expect(state()?.depth).toBe(1);

    fireEvent.click(within(layer('Представления таблицы')!).getByText('Реестр'));
    await settle();
    expect(layer('Представления таблицы')).toBeNull();
    expect(window.location.pathname).toBe('/tables/t.x/ready');

    window.history.back();
    await settle();
    await settle();
    // Пришли на запись страницы, а не на мёртвую запись поповера над ней.
    expect(window.location.pathname).toBe('/tables/t.x');
    expect(state()).toMatchObject({ idx: 0 });
    expect(state()?.layer).toBeUndefined();
  });

  it('«Назад» закрывает поповер условия отбора, как Esc; страница и адрес остаются, отбор прежний', async () => {
    const onChange = vi.fn();
    window.history.replaceState({ idx: 0, key: 'base', usr: null }, '', '/tables/t.x#f=1');
    render(
      <BrowserRouter>
        <HistoryLayersHost />
        <FilterChips columns={[{ name: 'Итого', label: 'Сумма', kind: 'number', operators: ['eq', 'gt'] }]}
          filter={{ type: 'condition', column: 'Итого', op: 'gt', value: '100' }} onChange={onChange} />
      </BrowserRouter>,
    );
    await settle();
    fireEvent.click(screen.getAllByRole('button', { name: /Сумма/ })[0]);
    await settle();
    expect(screen.queryByRole('button', { name: 'Применить' })).not.toBeNull();
    expect(state()?.depth).toBe(1);

    window.history.back();
    await settle();
    expect(screen.queryByRole('button', { name: 'Применить' })).toBeNull();
    expect(state()).toMatchObject({ idx: 0, key: 'base' });
    expect(state()?.layer).toBeUndefined();
    expect(window.location.href).toMatch(/\/tables\/t\.x#f=1$/);
    expect(onChange).not.toHaveBeenCalled();
  });
});
