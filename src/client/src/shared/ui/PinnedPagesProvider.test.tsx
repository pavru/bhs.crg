// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { USER_SETTINGS_KEY } from '@/shared/api/userSettings';
import { PinnedPagesProvider } from './PinnedPagesProvider';
import { usePinnedPages } from './pinnedPagesContext';

/**
 * Провайдер закреплений (work-04): зеркало браузера — только своему хозяину, а правка до прихода
 * настроек видна сразу и не выдаёт себя кэшу за ответ сервера.
 */

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
let email = 'a@example.test';
vi.mock('@/shared/api/client', () => ({ apiClient: api }));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { email } }) }));
vi.mock('./Snackbar', () => ({ useSnackbar: () => ({ apiError: vi.fn() }) }));

let client: QueryClient;
/** Список — в разметке, правка — кнопкой: тест смотрит туда же, куда человек. */
function Probe() {
  const pinned = usePinnedPages();
  return (
    <>
      <output aria-label="список">{JSON.stringify(pinned.list)}</output>
      <button type="button" onClick={() => pinned.pin('/waybills')}>закрепить</button>
    </>);
}
const shown = () => JSON.parse(screen.getByLabelText('список').textContent ?? '[]');

function mount() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><PinnedPagesProvider><Probe /></PinnedPagesProvider></QueryClientProvider>);
}

/** Ответ сервера, который придёт, когда тест скажет. */
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

beforeEach(() => {
  localStorage.clear();
  email = 'a@example.test';
  api.get.mockReset();
  api.put.mockReset();
});
afterEach(cleanup);

describe('PinnedPagesProvider', () => {
  it('зеркало своего хозяина — первым кадром; чужое — нет', () => {
    localStorage.setItem('crg-pinned', JSON.stringify({ who: 'a@example.test', value: '["/invoices"]' }));
    api.get.mockReturnValue(new Promise(() => {}));
    mount();
    expect(shown()).toEqual(['/invoices']);
    cleanup();

    email = 'b@example.test';
    mount();
    expect(shown()).toEqual([]);
  });

  it('правка до прихода настроек: видна сразу, кэш не засевается, на сервер — по свежему чтению', async () => {
    localStorage.setItem('crg-pinned', JSON.stringify({ who: 'a@example.test', value: '["/invoices"]' }));
    const first = deferred<{ data: Record<string, string> }>();
    api.get.mockReturnValue(first.promise);
    api.put.mockImplementation((_url: string, body: Record<string, string>) => Promise.resolve({ data: body }));
    mount();

    fireEvent.click(screen.getByRole('button', { name: 'закрепить' }));
    expect(shown()).toEqual(['/invoices', '/waybills']);
    // Пустой кэш с одним ключом читался бы ответом сервера без остальных предпочтений.
    expect(client.getQueryData(USER_SETTINGS_KEY)).toBeUndefined();

    await act(async () => { first.resolve({ data: { pinnedSections: '["/invoices"]', theme: 'dark' } }); });
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/account/settings',
      { pinnedSections: '["/invoices","/waybills"]' }));
    await waitFor(() => expect(shown()).toEqual(['/invoices', '/waybills']));
  });
});
