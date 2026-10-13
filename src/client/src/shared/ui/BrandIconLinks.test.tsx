// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BRANDING_KEY, type Branding } from '@/shared/api/branding';
import { apiClient } from '@/shared/api/client';
import { BrandIconLinks } from './BrandIconLinks';
import { BRAND_ICONS_KEY } from './brandIcons';

// Запрос оформления сам не отвечает: всё, что компонент узнаёт, тест кладёт в кеш — как это делают
// мутации экрана настройки. Ответить запросу велит себе только тест об ответе сервера прежней версии.
vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(() => new Promise(() => {})) } }));

const branding = (version: string): Branding => ({
  productName: 'Пример', isCustom: true, hasLogo: true, logoVersion: '1',
  icons: { state: 'custom', version, problem: null, aspect: 1 },
});

const tabHref = () => document.querySelector('link[rel="icon"]')?.getAttribute('href');

beforeEach(() => {
  document.head.innerHTML =
    '<link rel="icon" href="/api/branding/icons/tab"><link rel="apple-touch-icon" href="/api/branding/icons/apple">';
  localStorage.clear();
});

afterEach(cleanup);

/** Действие и всё, что из него следует: наблюдателей React Query извещает не в том же такте. */
const settled = (action: () => void) => act(async () => {
  action();
  await new Promise(resolve => setTimeout(resolve, 0));
});

function mounted() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(BRANDING_KEY, branding('A'));
  render(<QueryClientProvider client={client}><BrandIconLinks /></QueryClientProvider>);
  return client;
}

describe('ссылка значка следует за оформлением', () => {
  it('получив оформление, ставит ссылке адрес с меткой и запоминает метку', () => {
    mounted();

    expect(tabHref()).toBe('/api/branding/icons/tab?v=A');
    expect(document.querySelector('link[rel="apple-touch-icon"]')?.getAttribute('href'))
      .toBe('/api/branding/icons/apple?v=A');
    expect(localStorage.getItem(BRAND_ICONS_KEY)).toBe('A');
  });

  it('новая метка, пришедшая в кеш, доходит до ссылки', async () => {
    const client = mounted();

    await settled(() => client.setQueryData(BRANDING_KEY, branding('B')));

    expect(tabHref()).toBe('/api/branding/icons/tab?v=B');
  });

  // Выход из системы выбрасывает весь кеш (AuthProvider). Наблюдатель запроса при этом остаётся на
  // удалённом запросе, пока его компонент не перерисуется, — а этот не перерисовывается сам никогда.
  // Без подписки на кеш ссылка осталась бы на метке A до перезагрузки вкладки.
  it('и после того, как кеш ответов выброшен выходом из системы', async () => {
    const client = mounted();

    await settled(() => client.clear());
    await settled(() => client.setQueryData(BRANDING_KEY, branding('B')));

    expect(tabHref()).toBe('/api/branding/icons/tab?v=B');
    expect(localStorage.getItem(BRAND_ICONS_KEY)).toBe('B');
  });

  // Окно обновления: клиент уже новый, сервер ещё прежний и поля `icons` не знает. Компонент стоит
  // под корневой границей ошибок — упав на таком ответе, он снял бы всё приложение вместе со
  // страницей входа. Кеш здесь НЕ сеется: ответ идёт настоящим путём запроса, тем же, что в шапке.
  it('ответ сервера прежней версии, без значков, ссылку не трогает и ничего не запоминает', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce(
      { data: { productName: 'Пример', isCustom: true, hasLogo: true, logoVersion: '1' } } as never);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    await settled(() => { render(<QueryClientProvider client={client}><BrandIconLinks /></QueryClientProvider>); });

    // Ответ дошёл — иначе «ссылка не тронута» подтвердил бы и запрос, на который не ответили.
    expect(client.getQueryData<Branding>(BRANDING_KEY)?.productName).toBe('Пример');
    expect(tabHref()).toBe('/api/branding/icons/tab');
    expect(localStorage.getItem(BRAND_ICONS_KEY)).toBeNull();
  });
});
