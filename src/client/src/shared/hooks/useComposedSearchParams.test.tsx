// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation, useNavigate } from 'react-router';
import { useComposedSearchParams } from './useComposedSearchParams';

/** Записи адреса подряд складываются, а чужой переход их не воскрешает (shell-08). */

const wrapper = ({ children }: { children: ReactNode }) => (
  <MemoryRouter initialEntries={['/invoices']}>{children}</MemoryRouter>
);

function useHarness() {
  const [params, update] = useComposedSearchParams();
  return { params, update, navigate: useNavigate(), search: useLocation().search };
}

describe('useComposedSearchParams', () => {
  it('две записи в одном обработчике — обе в адресе', () => {
    const { result } = renderHook(useHarness, { wrapper });
    act(() => {
      result.current.update(p => p.delete('queue'), { replace: true });
      result.current.update(p => p.set('invoice', 'A'), { replace: true });
    });
    expect(result.current.search).toBe('?invoice=A');
  });

  it('переход мимо `update` вернул прежний адрес — правка начинается с него, а не с закрытого счёта', () => {
    const { result } = renderHook(useHarness, { wrapper });
    act(() => result.current.update(p => p.set('invoice', 'X')));
    expect(result.current.search).toBe('?invoice=X');
    // Ссылка «Счета» на открытом экране: адрес тот же, от которого писали счёт X.
    act(() => { void result.current.navigate('/invoices'); });
    expect(result.current.search).toBe('');
    act(() => result.current.update(p => p.set('queue', 'lost'), { replace: true }));
    expect(result.current.search).toBe('?queue=lost');
  });
  it('обработчик, взятый до ухода со счёта, открывает его снова («Вернуть» снэкбара)', () => {
    const { result } = renderHook(useHarness, { wrapper });
    act(() => result.current.update(p => p.set('invoice', 'X')));
    const held = result.current.update;
    act(() => result.current.update(p => p.delete('invoice'), { replace: true }));
    expect(result.current.search).toBe('');
    act(() => held(p => p.set('invoice', 'X'), { replace: true }));
    expect(result.current.search).toBe('?invoice=X');
  });
});
