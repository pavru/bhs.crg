// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { EntrySites } from '@/shared/api/workReports';
import { MyReportsPage } from './MyReportsPage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
vi.mock('@/shared/ui/usePaletteIntent', () => ({ usePaletteIntent: vi.fn() }));

beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

function mount(sites: EntrySites) {
  vi.mocked(apiClient.get).mockImplementation(async path => ({
    data: path === '/work/sites/mine' ? sites : { items: [], deleted: [], cursor: 0, more: false },
  }));
  render(<QueryClientProvider client={new QueryClient()}><MemoryRouter><MyReportsPage /></MemoryRouter></QueryClientProvider>);
}

it('скрывает создание при not-assigned и показывает причину', async () => {
  mount({ items: [], boundary: { text: 'ваши стройки (0)', count: 0, reason: 'not-assigned' } });
  expect(await screen.findByText('Вы не назначены ни на одну стройку — обратитесь к менеджеру проекта.')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Новый отчёт' })).toBeNull();
});

it('использует серверную подпись кнопки для ночной смены', async () => {
  mount({
    boundary: { text: 'ваши стройки (1)', count: 1, reason: null },
    items: [{ id: 'site', name: 'Стройка', today: '2026-11-01', timeZoneId: 'Asia/Vladivostok',
      suggested: { date: '2026-10-31', shift: 'night', label: 'Ночная смена 31.10' } }],
  });
  expect(await screen.findByRole('button', { name: 'Ночная смена 31.10' })).toBeTruthy();
});
