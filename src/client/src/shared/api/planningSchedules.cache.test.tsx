// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { scheduleKey, useCreateSchedule, useRebuildSchedule, useSchedule } from './planningSchedules';

vi.mock('./client', () => ({ apiClient: { post: vi.fn(), get: vi.fn() } }));

const writeResponse = { id: 'graph', constructionId: 'site', revisionId: 'rev', version: '4', works: [{ id: 'w1' }] };
const preview = { version: '3', fingerprint: 'seen', added: [], changed: [], removed: [], skipped: { unmapped: 0 }, removedLinks: [] };
const key = [...scheduleKey, writeResponse.constructionId, writeResponse.id];

function cache() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { wrapper, cached: () => client.getQueryData(key) };
}

afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('после перестроения сохраняет факт до повторного чтения и обновляет его ответом GET', async () => {
  const withFact = (version: string, volume: number, awaitingAcceptance: number) => ({
    ...writeResponse, version,
    works: [{ id: 'w1', fact: { state: 'partial', volume } }],
    factDiagnostics: { awaitingAcceptance },
  });
  const before = withFact('3', 12, 1);
  const updated = withFact('4', 30, 2);
  let finishRefresh!: () => void;
  const refetch = new Promise<{ data: typeof updated }>(resolve => { finishRefresh = () => resolve({ data: updated }); });
  vi.mocked(apiClient.get).mockResolvedValueOnce({ data: before }).mockReturnValueOnce(refetch);
  vi.mocked(apiClient.post).mockResolvedValue({ data: writeResponse });
  const { wrapper, cached } = cache();
  const { result } = renderHook(() => {
    useSchedule('site', 'graph');
    return useRebuildSchedule('graph');
  }, { wrapper });

  await waitFor(() => expect(cached()).toEqual(before));
  act(() => result.current.mutate(preview));
  await waitFor(() => expect(apiClient.get).toHaveBeenCalledTimes(2));
  expect(cached()).toEqual(before);
  finishRefresh();
  await waitFor(() => expect(cached()).toEqual(updated));
});

it('после построения карточка графика читает факт через GET', async () => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: writeResponse });
  const { wrapper, cached } = cache();
  const { result } = renderHook(() => useCreateSchedule(), { wrapper });

  await act(async () => {
    await result.current.mutateAsync({ constructionId: 'site', revisionId: 'rev', creationKey: 'create', name: 'График' });
  });
  expect(cached()).toBeUndefined();

  const full = { ...writeResponse, works: [{ id: 'w1', fact: { state: 'ok', volume: 12 } }] };
  vi.mocked(apiClient.get).mockResolvedValue({ data: full });
  renderHook(() => useSchedule('site', 'graph'), { wrapper });
  await waitFor(() => expect(cached()).toEqual(full));
});
