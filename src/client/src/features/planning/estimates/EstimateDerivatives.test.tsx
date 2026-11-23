// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { RevisionDerivatives } from '@/shared/api/planningDerivatives';
import { EstimateDerivatives } from './EstimateDerivatives';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
let rights: string[];
vi.mock('@/shared/api/access', () => ({ useCan: () => ({ permission: (code: string) => rights.includes(code) }) }));

const offer = { id: 'o1', number: 'КП-077', currentVersion: 2, status: 'sent' as const,
  date: '2026-11-23', sentOn: '2026-11-24', coverage: { covered: 4, total: 7, percent: 57.14 }, outdated: true, historical: true };
const first: RevisionDerivatives = { revisionId: 'r1', number: 1, offers: { state: 'ok', items: [offer] },
  schedules: { state: 'hidden', permission: 'plan.schedule.read' } };
const second: RevisionDerivatives = { revisionId: 'r2', number: 2,
  offers: { state: 'ok', items: [{ ...offer, historical: false, currentVersion: 3, number: 'КП-088' }] },
  schedules: { state: 'ok', items: [] } };

function mount(revisions: RevisionDerivatives[], revisionId = 'r1') {
  vi.mocked(apiClient.get).mockResolvedValue({ data: { revisions } });
  const client = new QueryClient();
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/estimates/e1']}>
    <EstimateDerivatives estimateId="e1" revisionId={revisionId} />
  </MemoryRouter></QueryClientProvider>);
}

afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('показывает покрытие и дату исторического КП только под выбранной редакцией', async () => {
  rights = ['plan.estimate.read', 'plan.offer.read'];
  mount([first, second]);
  const link = await screen.findByRole('link', { name: 'Открыть КП-077, версия 2' });
  expect(link.getAttribute('href')).toBe('/offers/o1?version=2');
  expect(screen.getByText(/4 из 7 · 57,14 %/)).toBeTruthy();
  expect(screen.getByText('Устарело')).toBeTruthy();
  expect(screen.getByText('24.11.2026')).toBeTruthy();
  expect(screen.queryByText('КП-088')).toBeNull();
});

it.each([true, false])('скрывает любые числа под закрытым блоком КП (серверный hidden %s)', async serverHidden => {
  rights = serverHidden ? ['plan.estimate.read', 'plan.offer.read'] : ['plan.estimate.read'];
  mount([serverHidden ? { ...first, offers: { state: 'hidden', permission: 'plan.offer.read' } } : first]);
  const block = await screen.findByRole('region', { name: 'КП редакции' });
  expect(within(block).getByText('plan.offer.read')).toBeTruthy();
  expect(block.textContent).not.toMatch(/\d/);
  expect(within(block).queryByRole('table')).toBeNull();
  expect(screen.getByText('plan.schedule.read')).toBeTruthy();
});
