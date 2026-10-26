// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { EstimateImportItem } from '@/shared/api/planningImports';
import { PendingImports } from './PendingImports';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

/** Полоса «ждёт решения» (plan-09, PR 2): прерванный разбор — не «разбирается», а отказ, который только убрать. */

const item = (over: Partial<EstimateImportItem>): EstimateImportItem => ({
  id: 'i1', target: { estimateId: 'e1', estimateName: 'ЭОМ', newEstimate: null }, fileName: 'ЛСР.gsfx', state: 'parsing',
  kind: null, refusalCode: null, interrupted: false, revisionId: null, createdAt: '2026-10-26T10:00:00Z',
  createdBy: null, version: '1', ...over,
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('полоса импортов, ждущих решения', () => {
  it('прерванный разбор назван прерванным, идущий — «разбирается», созданный не показан', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: [
      item({ id: 'i1', fileName: 'оборван.gsfx', interrupted: true }),
      item({ id: 'i2', fileName: 'идёт.gsfx' }),
      item({ id: 'i3', fileName: 'готов.gsfx', state: 'committed' }),
    ] } as never);
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PendingImports constructionId="c1" enabled onOpen={vi.fn()} />
      </QueryClientProvider>,
    );
    const broken = (await screen.findByText('оборван.gsfx')).closest('li')!;
    expect(broken.textContent).toContain('разбор прерван');
    expect(broken.textContent).not.toContain('разбирается');
    expect(screen.getByText('идёт.gsfx').closest('li')!.textContent).toContain('разбирается');
    expect(screen.queryByText('готов.gsfx')).toBeNull();
  });
});
