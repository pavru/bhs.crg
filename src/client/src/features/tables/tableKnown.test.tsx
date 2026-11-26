// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { AccessInfo } from '@/shared/api/access';
import { tableFilterColumns, type TableColumn } from '@/shared/api/tables';
import { conditionProblem } from '@/shared/filter/rowFilterModel';
import { RowFilterValue } from '@/shared/filter/RowFilterValue';
import type { FilterCondition } from '@/shared/api/types';
import { useTableKnown } from './tableKnown';

/**
 * Перечни колонок для подсказки отбора (D2 change `costs-filter-place-hints`): по адресу таблицы и по
 * флажкам колонок; справочники спрашиваются только тогда, когда перечень у таблицы есть.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
afterEach(() => { cleanup(); vi.mocked(apiClient.get).mockReset(); });

const column = (key: string, patch: Partial<TableColumn> = {}): TableColumn => ({
  key, label: key, kind: 'list', operators: ['eq', 'in'], system: true, unavailable: null, reason: null,
  dependsOnFilter: false, onDemand: false, note: null, options: null, requires: null, ...patch,
});
const OBJECTS = column('ОбъектыРазноски', { sites: true });
const SECTIONS = column('РазделыРазноски', { sections: true });
const SITES = [{ id: 'k', name: 'Комарова 36', sections: [{ id: 's', name: '4 эт.' }] }];
const REQUESTS_ONLY: AccessInfo = {
  permissions: ['costs.request.read', 'costs.request.edit'],
  modules: [{ code: 'costs', title: 'Счета и накладные', available: true }],
};
const CAN_READ_INVOICES: AccessInfo = { ...REQUESTS_ONLY, permissions: ['costs.invoice.read'] };

function answer(articles: 'ok' | 'fail' = 'ok') {
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/costs/constructions') return { data: SITES };
    if (url === '/costs/articles') {
      if (articles === 'fail') throw new Error('403');
      return { data: [{ id: 'a', name: 'Склад', archived: false }] };
    }
    throw new Error(`неожиданный адрес ${url}`);
  });
}

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}
const asked = () => vi.mocked(apiClient.get).mock.calls.map(([url]) => url);

describe('useTableKnown', () => {
  it('реестр счетов: перечни «Объекта» и «Раздела»; пока справочник едет — пусто и неполно', async () => {
    answer();
    const columns = [column('Номер', { kind: 'text' }), OBJECTS, SECTIONS];
    const { result } = renderHook(() => useTableKnown('costs.invoices', columns, CAN_READ_INVOICES), { wrapper });
    expect(result.current).toEqual({
      ОбъектыРазноски: expect.objectContaining({ items: [{ value: 'объект удалён' }], complete: false }),
      РазделыРазноски: expect.objectContaining({ complete: false }),
    });
    await waitFor(() => expect(result.current.ОбъектыРазноски.complete).toBe(true));
    expect(result.current.ОбъектыРазноски.items.map(i => i.value)).toEqual(['Комарова 36', 'Склад', 'объект удалён']);
    expect(result.current.РазделыРазноски.items.map(i => i.value))
      .toEqual(['Комарова 36 / 4 эт.', 'Комарова 36 / без раздела', 'раздел удалён']);
    expect(Object.keys(result.current)).not.toContain('Номер');
  });

  it('статьи не пришли — «Объект» неполон, «Раздел» полон', async () => {
    answer('fail');
    const { result } = renderHook(() => useTableKnown('costs.invoices', [OBJECTS, SECTIONS], CAN_READ_INVOICES), { wrapper });
    await waitFor(() => expect(result.current.РазделыРазноски.complete).toBe(true));
    await waitFor(() => expect(asked()).toContain('/costs/articles'));
    expect(result.current.ОбъектыРазноски.complete).toBe(false);
  });

  it('только «Раздел» — статьи не спрашиваются', async () => {
    answer();
    const { result } = renderHook(() => useTableKnown('costs.invoices', [SECTIONS], CAN_READ_INVOICES), { wrapper });
    await waitFor(() => expect(result.current.РазделыРазноски.complete).toBe(true));
    expect(asked()).toEqual(['/costs/constructions']);
  });

  it('чужая таблица со стройками и таблица без таких колонок — ни перечней, ни запросов', () => {
    answer();
    const other = renderHook(() => useTableKnown('plan.estimates', [OBJECTS], CAN_READ_INVOICES), { wrapper });
    const plain = renderHook(
      () => useTableKnown('costs.invoices', [column('Номер', { kind: 'text' })], CAN_READ_INVOICES), { wrapper },
    );
    expect(other.result.current).toEqual({});
    expect(plain.result.current).toEqual({});
    expect(apiClient.get).not.toHaveBeenCalled();
  });

  it('одни права заявок открывают реестр без запросов справочников счетов', () => {
    const { result } = renderHook(() => useTableKnown('costs.invoices', [OBJECTS, SECTIONS], REQUESTS_ONLY), { wrapper });
    expect(result.current).toEqual({});
    expect(apiClient.get).not.toHaveBeenCalled();

    const money = column('Сумма', { kind: 'number', unavailable: 'no-right', reason: 'нет права на суммы' });
    const cols = tableFilterColumns([OBJECTS, SECTIONS, money], result.current);
    const condition: FilterCondition = { type: 'condition', column: OBJECTS.key, op: 'eq', value: 'Комарова 36' };
    const change = vi.fn();
    render(<RowFilterValue cond={condition} column={cols[0]} onChange={change} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Значение' }), { target: { value: 'Другая стройка' } });
    expect(change).toHaveBeenCalledWith({ ...condition, value: 'Другая стройка' });
    expect(conditionProblem({ ...condition, column: SECTIONS.key }, cols)).toBeNull();
    expect(conditionProblem({ ...condition, column: money.key, value: '10' }, cols)).toContain('нет права на суммы');
  });
});
