// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { CorrectionPreview } from '@/shared/api/invoiceCorrection';
import { CorrectionPreviewDialog } from './CorrectionPreviewDialog';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/** Предпросмотр исправления: смена режима НДС — отдельным пунктом (issue #1498). */

afterEach(cleanup);

beforeEach(() => {
  vi.mocked(apiClient.get).mockResolvedValue({ data: [] } as never);
});

const preview = (overrides: Partial<CorrectionPreview>): CorrectionPreview => ({
  version: 1, day: '2026-10-19', stamp: 's',
  records: [{ kind: 'correction', day: '2026-10-19', tolerance: 1, lines: [] }],
  ...overrides,
});

function mount(shown: CorrectionPreview) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <CorrectionPreviewDialog preview={shown} pending={false} error={null} reason="цены без НДС" note={null}
        writing={false} onBack={() => {}} onWrite={() => {}} />
    </QueryClientProvider>,
  );
}

describe('предпросмотр исправления', () => {
  it('смена режима — пунктом, и запись без денег называет, что ещё ляжет', () => {
    mount(preview({ vatMode: { from: 'Included', to: 'OnTop' } }));
    expect(screen.getByText('Цены в строках: с НДС → без НДС')).toBeTruthy();
    expect(screen.getByText(/запишутся только версия, причина и режим НДС/)).toBeTruthy();
  });

  it('без смены режима пункта нет; старый сервер поля не присылает — так же', () => {
    mount(preview({ vatMode: null }));
    expect(screen.queryByText(/Цены в строках/)).toBeNull();
    expect(screen.getByText(/запишутся только версия и причина/)).toBeTruthy();
    cleanup();
    mount(preview({}));
    expect(screen.queryByText(/Цены в строках/)).toBeNull();
  });
});
