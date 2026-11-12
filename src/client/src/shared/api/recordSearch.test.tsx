// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from './client';
import { searchQuery, useRecordSearch, type RecordSearchGroup } from './recordSearch';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));

afterEach(() => { cleanup(); vi.resetAllMocks(); vi.useRealTimers(); });

function wrapper() {
  const client = new QueryClient();
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const group = (code: string, title: string): RecordSearchGroup =>
  ({ code, title, module: 'costs', recordType: 'СчётНаОплату', items: [{ id: code, title }], more: false });

/** Поиск записей палитры ([UI-30], shell-05 задача 2.1). */
describe('useRecordSearch', () => {
  it('запрос короче двух знаков сервер не спрашивает', () => {
    expect(searchQuery(' с ')).toBeNull();
    expect(searchQuery('  щит   1 ')).toBe('щит 1');
    // Слово меряется, а не строка: «а б» не уже, чем «а»; знаки по краям не слово.
    expect(searchQuery('а б')).toBeNull();
    expect(searchQuery('№ · «')).toBeNull();
    expect(searchQuery('№ 418')).toBe('№ 418');
    expect(searchQuery('щ'.repeat(201))).toBeNull();
    renderHook(() => useRecordSearch('с'), { wrapper: wrapper() });
    expect(apiClient.get).not.toHaveBeenCalled();
  });

  // Группы отбирает палитра из ответа «везде»: чип не повод спрашивать снова.
  it('спрашивает после паузы ввода, без области', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: { groups: [group('invoices', '№ 418')] } });
    const { result } = renderHook(() => useRecordSearch('418'), { wrapper: wrapper() });

    expect(result.current.pending).toBe(true);
    await waitFor(() => expect(result.current.groups?.[0].title).toBe('№ 418'));
    const [url, config] = vi.mocked(apiClient.get).mock.calls[0];
    expect(url).toBe('/search');
    expect(config?.params).toEqual({ q: '418' });
    expect(config?.signal).toBeInstanceOf(AbortSignal);
  });

  it('по данным не искать — адрес не зовётся', async () => {
    renderHook(() => useRecordSearch('418', false), { wrapper: wrapper() });
    await new Promise(r => setTimeout(r, 300));
    expect(apiClient.get).not.toHaveBeenCalled();
  });

  it('ответ на прежний запрос не показывается, пока новый не отстоялся', async () => {
    vi.mocked(apiClient.get).mockImplementation((_url, config) =>
      Promise.resolve({ data: { groups: [group('invoices', `ответ на ${config?.params.q}`)] } }));
    const { result, rerender } = renderHook(({ q }) => useRecordSearch(q),
      { wrapper: wrapper(), initialProps: { q: '41' } });
    await waitFor(() => expect(result.current.groups?.[0].title).toBe('ответ на 41'));

    rerender({ q: '418' });
    // Сразу после клавиши: ответ на «41» уже не ответ — групп нет, поиск идёт.
    expect(result.current.groups).toBeUndefined();
    expect(result.current.pending).toBe(true);
    await waitFor(() => expect(result.current.groups?.[0].title).toBe('ответ на 418'));
  });

  it('отказ адреса — отказ, а не пусто, и без повтора', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(new Error('500'));
    const { result } = renderHook(() => useRecordSearch('418'), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.failed).toBe(true));
    expect(result.current.groups).toBeUndefined();
    expect(apiClient.get).toHaveBeenCalledTimes(1);
  });

  // Предел — признак, а не тишина; и только когда поиск включён: на чипе «Разделы» записи не ищут вовсе.
  it('длиннее предела — сказано, и только при включённом поиске', () => {
    const long = 'щ'.repeat(201);
    expect(renderHook(() => useRecordSearch(long), { wrapper: wrapper() }).result.current.tooLong).toBe(true);
    expect(renderHook(() => useRecordSearch(long, false), { wrapper: wrapper() }).result.current.tooLong).toBe(false);
    // Длинный запрос сервер не спрашивают вовсе — отказ сказан клиентом.
    expect(apiClient.get).not.toHaveBeenCalled();
    expect(renderHook(() => useRecordSearch('щит'), { wrapper: wrapper() }).result.current.tooLong).toBe(false);
  });
});
