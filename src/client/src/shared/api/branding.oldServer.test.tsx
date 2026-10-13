// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from './client';
import {
  BRANDING_KEY, useBranding, useRemoveLogo, useSaveProductName, useUploadLogo, type Branding,
} from './branding';

vi.mock('./client', () => ({ apiClient: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

// Окно обновления: клиент уже новый, сервер ещё прежний и поля `icons` не знает. Писателей в кеш
// оформления четыре, и ответ без поля, положенный любым из них, роняет читателей — а один из них,
// ссылка значка, стоит под корневой границей ошибок, то есть вместе с ним упало бы всё приложение.
// Сама подстановка проверена в `branding.test.ts`; здесь — что мимо неё не ходит ни один писатель.
// Живой прогон этого не поймает: сервер стенда отдаёт поле всегда.
const old = { data: { productName: 'Пример', isCustom: true, hasLogo: true, logoVersion: '1' } } as never;
const NO_ICONS = { state: 'standard', version: '', problem: null, aspect: null };

function cache() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) =>
    <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { wrapper, icons: () => client.getQueryData<Branding>(BRANDING_KEY)?.icons };
}

describe('ответ сервера прежней версии ложится в кеш уже со значками', () => {
  afterEach(() => { cleanup(); vi.clearAllMocks(); });

  it('чтение оформления', async () => {
    vi.mocked(apiClient.get).mockResolvedValue(old);
    const { wrapper, icons } = cache();

    renderHook(() => useBranding(), { wrapper });

    await waitFor(() => expect(icons()).toEqual(NO_ICONS));
  });

  it('сохранение названия', async () => {
    vi.mocked(apiClient.put).mockResolvedValue(old);
    const { wrapper, icons } = cache();
    const { result } = renderHook(() => useSaveProductName(), { wrapper });

    await act(async () => { await result.current.mutateAsync('Пример'); });

    expect(icons()).toEqual(NO_ICONS);
  });

  it('загрузка логотипа', async () => {
    vi.mocked(apiClient.post).mockResolvedValue(old);
    const { wrapper, icons } = cache();
    const { result } = renderHook(() => useUploadLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(new File(['x'], 'logo.png')); });

    expect(icons()).toEqual(NO_ICONS);
  });

  it('снятие логотипа', async () => {
    vi.mocked(apiClient.delete).mockResolvedValue(old);
    const { wrapper, icons } = cache();
    const { result } = renderHook(() => useRemoveLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(); });

    expect(icons()).toEqual(NO_ICONS);
  });
});
