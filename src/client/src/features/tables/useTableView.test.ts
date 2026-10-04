import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { FilterNode } from '@/shared/api/types';
import { DEFAULT_VIEW, parseView, viewHash, withFilter, withSort } from './tableViewState';

/**
 * Хук состояния таблицы: изменение считается от адреса БРАУЗЕРА, а не от адреса последней отрисовки.
 *
 * Экрана в тестах нет, поэтому хук зовётся как функция: `useMemo` и `useCallback` подменены прямым
 * вызовом, маршрутизатор — парой «адрес отрисовки» и «куда записали». Стережётся ровно то, чем
 * починка отличается от поломки: откуда хук берёт текущее состояние. Чистая `addressChange` этого
 * не видит — её тесты зелёные и при чтении из `useLocation`.
 */

const router = vi.hoisted(() => ({
  rendered: { hash: '', pathname: '/tables/costs.invoices' },
  navigate: vi.fn(),
}));

vi.mock('react', () => ({
  useMemo: <T>(make: () => T) => make(),
  useCallback: <T>(fn: T) => fn,
}));
vi.mock('react-router', () => ({
  useLocation: () => router.rendered,
  useNavigate: () => router.navigate,
}));

import { useTableView } from './useTableView';

const purpose: FilterNode = { type: 'condition', column: 'Назначение', op: 'eq', value: 'Посев' };
const unpaid: FilterNode = { type: 'condition', column: 'СостояниеОплаты', op: 'eq', value: 'Не оплачен' };
const both: FilterNode = { type: 'group', logic: 'and', children: [purpose, unpaid] };

function browserAt(pathname: string, hash: string) {
  vi.stubGlobal('window', { location: { pathname, hash } });
}

beforeEach(() => {
  router.navigate.mockReset();
  router.rendered = { hash: '', pathname: '/tables/costs.invoices' };
});
afterEach(() => vi.unstubAllGlobals());

describe('useTableView — действие в просвете между адресом и перерисовкой', () => {
  it('щелчок по шапке считается от адреса браузера: условие, добавленное мгновением раньше, цело', () => {
    // Экран нарисован под одним условием, а в адресе браузера их уже два.
    router.rendered.hash = viewHash({ ...DEFAULT_VIEW, filter: purpose });
    browserAt('/tables/costs.invoices', viewHash({ ...DEFAULT_VIEW, filter: both }));

    const [view, setView] = useTableView();
    expect(view.filter).toEqual(purpose);
    setView(v => withSort(v, 'Номер', false));

    expect(router.navigate).toHaveBeenCalledTimes(1);
    const [to, options] = router.navigate.mock.calls[0] as [{ hash: string }, { replace: boolean }];
    expect(parseView(to.hash).filter).toEqual(both);
    expect(parseView(to.hash).sort).toEqual([{ column: 'Номер', descending: false }]);
    // От адреса браузера отбор не сменился — запись истории заменяется.
    expect(options.replace).toBe(true);
  });

  it('смена отбора — новая запись истории; без отличий записи нет', () => {
    browserAt('/tables/costs.invoices', '');
    const [, setView] = useTableView();

    setView(v => v);
    expect(router.navigate).not.toHaveBeenCalled();

    setView(v => withFilter(v, unpaid));
    expect(router.navigate.mock.calls[0][1]).toEqual({ replace: false });
  });

  it('адрес уже чужой — экран уходит, и опоздавшее действие ничего не пишет', () => {
    // Щёлкнули «Таблица целиком» из реестра: путь в браузере другой, а нарисован ещё реестр.
    router.rendered.pathname = '/tables/costs.invoices/registry';
    browserAt('/tables/costs.invoices', viewHash({ ...DEFAULT_VIEW, filter: purpose }));

    const [, setView] = useTableView();
    setView(v => withSort(v, 'Номер', false));

    expect(router.navigate).not.toHaveBeenCalled();
  });
});
