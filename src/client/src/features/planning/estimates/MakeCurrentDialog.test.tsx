// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { CurrentRevisionPreview } from '@/shared/api/planningEstimates';
import { MakeCurrentDialog } from './MakeCurrentDialog';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
const success = vi.fn();
vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ success, error: vi.fn(), apiError: vi.fn() }) }));

/**
 * «Сделать действующей» (plan-09, задача 2.4, ТЗ PLAN-5.2, решение В-5): расхождения — с сервера; отказ 409
 * оставляет окно открытым на той же редакции, перечитывает предпросмотр и называет причину.
 */

let version = '7';
const preview = (): CurrentRevisionPreview => ({
  revisionId: 'r3', revisionNumber: 3, currentRevisionNumber: 2, version, unmatched: 17,
  changes: [{ workTypeId: 'w1', unitId: null, was: 9840, becomes: 10200 }],
});

beforeEach(() => {
  version = '7';
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/plan/estimates/e1/current-revision/preview') return { data: preview() } as never;
    if (url === '/work-types') return { data: [{ id: 'w1', displayName: 'Прокладка кабеля до 1 кВ' }] } as never;
    throw new Error(`неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

function mount(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MakeCurrentDialog estimateId="e1" revision={{ id: 'r3', number: 3 }} onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

describe('сделать редакцию действующей', () => {
  it('показывает расхождения сервера и напоминает о несопоставленных', async () => {
    mount();
    expect(await screen.findByText('Прокладка кабеля до 1 кВ')).toBeTruthy();
    expect(screen.getByText(/вместо ред\. 2/)).toBeTruthy();
    expect(screen.getByText(/не сопоставлено строк: 17/)).toBeTruthy();
  });

  it('409: окно открыто на той же редакции, предпросмотр перечитан, повтор — по новой версии', async () => {
    const onClose = mount();
    await screen.findByText('Прокладка кабеля до 1 кВ');
    vi.mocked(apiClient.post).mockRejectedValueOnce({
      response: { status: 409, data: { error: 'Смету тем временем изменили, и это действие не выполнено.' } },
    });
    version = '8';
    fireEvent.click(screen.getByRole('button', { name: 'Сделать действующей' }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByText(/тем временем изменили/)).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
    expect(apiClient.post).toHaveBeenCalledWith('/plan/estimates/e1/current-revision', { revisionId: 'r3' },
      { headers: { 'If-Match': '7' } });

    // Предпросмотр перечитан — смена идёт по версии, которую человек теперь видит, и той же редакцией.
    await waitFor(() => expect(vi.mocked(apiClient.get).mock.calls
      .filter(([url]) => url === '/plan/estimates/e1/current-revision/preview')).toHaveLength(2));
    vi.mocked(apiClient.post).mockResolvedValueOnce({ data: { id: 'e1' } } as never);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сделать действующей' }).hasAttribute('disabled')).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Сделать действующей' }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(apiClient.post).toHaveBeenLastCalledWith('/plan/estimates/e1/current-revision', { revisionId: 'r3' },
      { headers: { 'If-Match': '8' } });
    expect(success).toHaveBeenCalled();
  });
});
