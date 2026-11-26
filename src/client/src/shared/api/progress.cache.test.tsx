// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { useSchedule } from './planningSchedules';
import { useConstructionProgress } from './progress';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('перечитывает ход работ и подробности графика при возврате с тёплым кэшем', async () => {
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  let accepted = false;
  vi.mocked(apiClient.get).mockImplementation(async url => {
    const volume = accepted ? 12 : null;
    return { data: url === '/plan/schedules/graph'
      ? { works: [{ fact: { volume } }] }
      : { rows: [{ cells: { fact: { volume } } }] } };
  });
  const useViews = () => ({
    progress: useConstructionProgress('site', 'section'),
    schedule: useSchedule('site', 'graph'),
  });

  const first = renderHook(useViews, { wrapper });
  await waitFor(() => {
    expect(first.result.current.progress.data?.rows[0].cells.fact?.volume).toBeNull();
    expect(first.result.current.schedule.data?.works[0].fact).toEqual({ volume: null });
  });
  first.unmount();
  accepted = true;
  const returned = renderHook(useViews, { wrapper });
  await waitFor(() => {
    expect(returned.result.current.progress.data?.rows[0].cells.fact?.volume).toBe(12);
    expect(returned.result.current.schedule.data?.works[0].fact).toEqual({ volume: 12 });
  });
  expect(apiClient.get).toHaveBeenCalledTimes(4);
});
