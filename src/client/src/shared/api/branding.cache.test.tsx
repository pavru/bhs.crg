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

// Что лежит в кеше оформления после каждого из четырёх его писателей — чтения и трёх правок.
// Писателя зовём настоящего, через его хук: проверяется проводка, а не функция в одиночку.

function cache() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) =>
    <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { wrapper, cached: () => client.getQueryData<Branding>(BRANDING_KEY) };
}

afterEach(() => { cleanup(); vi.resetAllMocks(); });

// Окно обновления: клиент уже новый, сервер ещё прежний и поля `icons` не знает. Ответ без поля,
// положенный любым писателем, роняет читателей — а один из них, ссылка значка, стоит под корневой
// границей ошибок, то есть вместе с ним упало бы всё приложение. Сама подстановка проверена в
// `branding.test.ts`; здесь — что мимо неё не ходит ни один писатель. Живой прогон этого не
// поймает: сервер стенда отдаёт поле всегда.
describe('ответ сервера прежней версии ложится в кеш уже со значками', () => {
  const old = { data: { productName: 'Пример', isCustom: true, hasLogo: true, logoVersion: '1' } } as never;
  const NO_ICONS = { state: 'standard', version: '', problem: null, aspect: null };

  it('чтение оформления', async () => {
    vi.mocked(apiClient.get).mockResolvedValue(old);
    const { wrapper, cached } = cache();

    renderHook(() => useBranding(), { wrapper });

    await waitFor(() => expect(cached()?.icons).toEqual(NO_ICONS));
  });

  it('сохранение названия', async () => {
    vi.mocked(apiClient.put).mockResolvedValue(old);
    const { wrapper, cached } = cache();
    const { result } = renderHook(() => useSaveProductName(), { wrapper });

    await act(async () => { await result.current.mutateAsync('Пример'); });

    expect(cached()?.icons).toEqual(NO_ICONS);
  });

  it('загрузка логотипа', async () => {
    vi.mocked(apiClient.post).mockResolvedValue(old);
    const { wrapper, cached } = cache();
    const { result } = renderHook(() => useUploadLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(new File(['x'], 'logo.png')); });

    expect(cached()?.icons).toEqual(NO_ICONS);
  });

  it('снятие логотипа', async () => {
    vi.mocked(apiClient.delete).mockResolvedValue(old);
    const { wrapper, cached } = cache();
    const { result } = renderHook(() => useRemoveLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(); });

    expect(cached()?.icons).toEqual(NO_ICONS);
  });
});

// Экран настройки переспрашивает оформление раз в минуту, пока значки не построены, и такой запрос
// сам запускает на сервере перестроение — до пятнадцати секунд. Правка, сделанная в эти секунды,
// отвечает раньше, а запрос возвращается следом со снимком, снятым ДО неё. Без отмены запроса в
// полёте он затёр бы ответ правки: «Логотип убран.» — и логотип снова на экране.
describe('ответ правки не затирается запросом оформления, ушедшим до неё', () => {
  const icons = { state: 'failed', version: 'std', problem: 'builder', aspect: null } as const;
  const before: Branding = { productName: 'Прежнее', isCustom: true, hasLogo: true, logoVersion: '1', icons };
  const after: Branding = { productName: 'Новое', isCustom: true, hasLogo: false, logoVersion: null,
    icons: { state: 'standard', version: 'std', problem: null, aspect: null } };

  /** Запрос оформления в полёте — и рычаг, которым он отвечает снимком до правки. */
  function pollInFlight(wrapper: ({ children }: { children: ReactNode }) => ReactNode) {
    let answer!: (response: unknown) => void;
    vi.mocked(apiClient.get).mockReturnValue(new Promise(resolve => { answer = resolve; }) as never);
    renderHook(() => useBranding(), { wrapper });
    return () => act(async () => {
      answer({ data: before });
      await new Promise(resolve => setTimeout(resolve, 0));
    });
  }

  it('сохранение названия', async () => {
    const { wrapper, cached } = cache();
    const lateAnswer = pollInFlight(wrapper);
    vi.mocked(apiClient.put).mockResolvedValue({ data: after } as never);
    const { result } = renderHook(() => useSaveProductName(), { wrapper });

    await act(async () => { await result.current.mutateAsync('Новое'); });
    await lateAnswer();

    expect(cached()).toEqual(after);
  });

  it('загрузка логотипа', async () => {
    const { wrapper, cached } = cache();
    const lateAnswer = pollInFlight(wrapper);
    vi.mocked(apiClient.post).mockResolvedValue({ data: after } as never);
    const { result } = renderHook(() => useUploadLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(new File(['x'], 'logo.png')); });
    await lateAnswer();

    expect(cached()).toEqual(after);
  });

  it('снятие логотипа', async () => {
    const { wrapper, cached } = cache();
    const lateAnswer = pollInFlight(wrapper);
    vi.mocked(apiClient.delete).mockResolvedValue({ data: after } as never);
    const { result } = renderHook(() => useRemoveLogo(), { wrapper });

    await act(async () => { await result.current.mutateAsync(); });
    await lateAnswer();

    expect(cached()).toEqual(after);
  });
});
