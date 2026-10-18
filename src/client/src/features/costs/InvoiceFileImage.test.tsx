// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import type { PrintProfile } from '@/shared/api/printProfiles';
import { BuiltImage } from './InvoiceFileImage';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * Панель вида для чтения (issue #1456): строку о профиле и выбор профиля видит только тот, кто может
 * перестроить; выбор перестраивает по выбранному профилю.
 */

const profile = (id: string, name: string, isSystem = false): PrintProfile => ({
  id, name, paper: 'A4', orientation: isSystem ? 'portrait' : 'landscape', fit: 'width',
  margins: { top: 5, bottom: 5, left: 5, right: 5 }, isSystem, isFactory: true, factory: null,
});
const PROFILES = [profile('sys', 'по умолчанию', true), profile('wide', 'Альбомный')];

const image = (over: Partial<InvoiceImage> = {}): InvoiceImage => ({
  state: 'built', pages: 1, notes: [], converter: null, builtAt: null, reason: null, aboutFile: false,
  layout: 'profile', profile: { id: 'sys', name: 'по умолчанию' }, profileState: 'changed', ...over,
});

let filesUse = true;

afterEach(cleanup);
beforeEach(() => {
  filesUse = true;
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.post).mockReset();
  vi.mocked(apiClient.get).mockImplementation((async (url: string) => {
    if (url === '/settings/print-profiles') return { data: PROFILES };
    if (url === '/account/access') return { data: { permissions: filesUse ? ['core.files.use'] : [], modules: [] } };
    throw new Error('вида в тесте нет');
  }) as never);
});

function shown(view: InvoiceImage, canEdit: boolean) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ToastProvider>
        <BuiltImage invoiceId="i1" blobPath="files/a.xlsx" fileName="Счёт.xlsx" image={view} canEdit={canEdit} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

describe('вид для чтения: профиль печати', () => {
  it('читателю — ни строки о профиле, ни выбора, и список профилей не спрашивается', async () => {
    shown(image(), false);
    await waitFor(() => expect(screen.getByTestId('image-caveat')).toBeTruthy());
    expect(screen.queryByTestId('layout-notice')).toBeNull();
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(false);
  });

  it('тому, кто может перестроить, — строка «изменён» и чип; выбор перестраивает по выбранному', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: image({ profile: { id: 'wide', name: 'Альбомный' }, profileState: 'current' }) } as never);
    shown(image(), true);
    expect(screen.getByTestId('layout-notice').textContent).toContain('изменён после построения вида');
    fireEvent.click(await screen.findByTestId('layout-chip'));
    const sys = await screen.findByRole('radio', { name: /по умолчанию/ });
    expect(sys.getAttribute('aria-checked')).toBe('true');
    expect(sys.textContent).toContain('сейчас');
    fireEvent.click(screen.getByRole('radio', { name: /Альбомный/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Перестроить по выбранному' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/costs/invoices/i1/scan/image', { printProfileId: 'wide' }));
    expect(await screen.findByText('Вид для чтения перестроен по профилю «Альбомный».')).toBeTruthy();
  });

  it('без права работы с файлами выбора нет, и список профилей не спрашивается — а строка «изменён» есть', async () => {
    filesUse = false;
    shown(image(), true);
    expect(screen.getByTestId('layout-notice').textContent).toContain('изменён после построения вида');
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/account/access')).toBe(true));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/settings/print-profiles')).toBe(false);
  });

  it('у книги со своей раскладкой выбора нет, а «Перестроить» — без профиля', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: image({ layout: 'file', profile: null, profileState: null }) } as never);
    shown(image({ layout: 'file', profile: null, profileState: null }), true);
    fireEvent.click(screen.getByRole('button', { name: /Перестроить/ }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/costs/invoices/i1/scan/image', undefined));
    expect(screen.queryByTestId('layout-chip')).toBeNull();
    expect(screen.queryByTestId('layout-notice')).toBeNull();
  });
});
