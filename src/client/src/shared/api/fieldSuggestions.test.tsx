// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from './client';
import { useFieldSuggestions, type SuggestSource } from './fieldSuggestions';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));

afterEach(() => { cleanup(); vi.resetAllMocks(); });

const SOURCE: SuggestSource = { type: 't1', field: 'mark', scope: 'Set', scopeId: 's1' };

function wrapper() {
  const client = new QueryClient();
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useFieldSuggestions (UI-5)', () => {
  it('зовёт адрес с источником и набранным, отдаёт пункты', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: { items: [{ value: 'ВВГ', here: true }] } });
    const { result } = renderHook(() => useFieldSuggestions(SOURCE, 'вв'), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.data).toEqual([{ value: 'ВВГ', here: true }]));
    const [url, config] = vi.mocked(apiClient.get).mock.calls[0];
    expect(url).toBe('/field-suggestions');
    expect(config?.params).toEqual({ type: 't1', field: 'mark', q: 'вв', scope: 'Set', scopeId: 's1' });
    expect(config?.signal).toBeInstanceOf(AbortSignal);
  });

  it('тот же запрос в пределах минуты — один вызов адреса', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: { items: [] } });
    const shared = wrapper();
    const first = renderHook(() => useFieldSuggestions(SOURCE, 'Каб'), { wrapper: shared });
    await waitFor(() => expect(first.result.current.isSuccess).toBe(true));
    first.unmount();

    const again = renderHook(() => useFieldSuggestions(SOURCE, 'Каб'), { wrapper: shared });
    await waitFor(() => expect(again.result.current.isSuccess).toBe(true));
    expect(apiClient.get).toHaveBeenCalledTimes(1);
  });

  it('ничего не набрано — адрес не зовётся', () => {
    renderHook(() => useFieldSuggestions(SOURCE, null), { wrapper: wrapper() });
    renderHook(() => useFieldSuggestions(null, 'Каб'), { wrapper: wrapper() });
    expect(apiClient.get).not.toHaveBeenCalled();
  });

  it('отказ адреса — ошибка, а не пустой список, и без повтора', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(Object.assign(new Error('503'), { response: { status: 503 } }));
    const { result } = renderHook(() => useFieldSuggestions(SOURCE, 'Каб'), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.data).toBeUndefined();
    expect(apiClient.get).toHaveBeenCalledTimes(1);
  });
});
