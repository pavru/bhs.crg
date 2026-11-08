// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { currentBatch, resetBatchForTests, startBatch, type BatchPort } from '@/features/costs/scanBatch';
import { ActivityCenter } from './ActivityCenter';

/**
 * Пакет счетов из файлов в центре активности (costs-invoice-screen-14, D5): там, где списка нет, ход с
 * «Остановить» и итог видны здесь, и итог сам не исчезает. Второй кнопки в шапке нет.
 */

vi.mock('@/shared/api/jobs', () => ({ useActiveJobs: () => [], useCancelJob: () => ({ mutate: vi.fn(), isPending: false }) }));
vi.mock('@/shared/api/notifications', () => ({
  useNotifications: () => ({ data: { items: [], unreadCount: 0 } }),
  useHealth: () => ({ data: [] }),
  useMarkNotificationRead: () => ({ mutate: vi.fn() }),
  useMarkAllNotificationsRead: () => ({ mutate: vi.fn() }),
  useDismissNotification: () => ({ mutate: vi.fn() }),
  useClearNotifications: () => ({ mutate: vi.fn() }),
}));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { sub: 'я' } }) }));
vi.mock('@/shared/api/fileKinds', async real => ({ ...(await real<object>()), useFileKinds: () => ({ data: undefined }) }));

beforeAll(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
beforeEach(() => resetBatchForTests());
afterEach(cleanup);

let release: () => void = () => {};
const port = (): BatchPort => ({
  owner: 'я', kinds: undefined, refresh: () => {},
  fingerprint: async f => f.name.padEnd(64, '0'),
  ask: async prints => prints.map(fingerprint => ({ fingerprint, twins: [] })),
  send: file => new Promise(resolve => { release = () => resolve({ id: `id-${file.name}`, sameFile: [] }); }),
});
const pdf = (name: string) => new File(['x'], name, { type: 'application/pdf' });

function shown() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter><NavigationGuardProvider><ActivityCenter /></NavigationGuardProvider></MemoryRouter>
    </QueryClientProvider>);
  return screen.getByTestId('activity');
}

const until = async (ok: () => boolean) => { for (let i = 0; i < 200 && !ok(); i++) await act(() => new Promise(r => setTimeout(r, 0))); };

describe('центр активности: загрузка счетов', () => {
  it('пакет идёт — пилюля с ходом, в поповере «Загрузка счетов» с «Остановить»', async () => {
    startBatch([pdf('а.pdf'), pdf('б.pdf')], [], port());
    await until(() => currentBatch()?.phase === 'running');
    const button = shown();
    expect(button.getAttribute('aria-label')).toBe('Выполняется: Счета из файлов · 0 из 2; новых уведомлений нет');
    await userEvent.setup().click(button);
    const section = screen.getByTestId('activity-scan-batch');
    expect(within(section).getByRole('heading', { name: 'Загрузка счетов' })).toBeTruthy();
    expect(within(section).getByRole('button', { name: 'Остановить' })).toBeTruthy();
    // Отбора списка здесь нет: списка нет.
    expect(within(section).queryByRole('button', { name: 'Показать только их' })).toBeNull();
  });

  it('итог сам не исчезает и считается среди новых, закрывает его крестик', async () => {
    startBatch([pdf('а.pdf')], [], port());
    await until(() => currentBatch()?.phase === 'running');
    release();
    await until(() => currentBatch()?.phase === 'done');
    const button = shown();
    expect(button.getAttribute('aria-label')).toBe('Уведомления — новых уведомлений: 1, итог загрузки счетов');
    await userEvent.setup().click(button);
    const section = screen.getByTestId('activity-scan-batch');
    expect(within(section).getByRole('heading', { name: 'Загрузка счетов · итог' })).toBeTruthy();
    await userEvent.setup().click(within(section).getByRole('button', { name: 'Закрыть' }));
    expect(currentBatch()).toBeNull();
    expect(screen.queryByTestId('activity-scan-batch')).toBeNull();
  });

  it('пакета нет — раздела нет', async () => {
    await userEvent.setup().click(shown());
    expect(screen.queryByTestId('activity-scan-batch')).toBeNull();
  });
});
