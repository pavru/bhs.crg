// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { EntrySites, WorkReport } from '@/shared/api/workReports';
import { ReportForm } from './ReportForm';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { displayName: 'Иванов' } }) }));

const sites: EntrySites = {
  items: [
    { id: 'site-a', name: 'Стройка А', today: '2026-11-01', timeZoneId: 'Asia/Vladivostok',
      suggested: { date: '2026-10-31', shift: 'night', label: 'Ночная смена 31.10' } },
    { id: 'site-b', name: 'Стройка Б', today: '2026-10-31', timeZoneId: 'Europe/Moscow',
      suggested: { date: '2026-10-31', shift: 'day', label: 'Отчёт 31.10' } },
  ],
  boundary: { text: 'ваши стройки (2)', count: 2, reason: null },
};
const report: WorkReport = {
  id: 'r1', constructionId: 'site-a', employeeId: 'e1', employeeName: 'Петров', constructionName: 'Стройка А',
  date: '2026-10-31', shift: 'day', status: 'draft', version: '1', periodClosed: false, hours: 8, note: null,
  today: '2026-11-01', timeZoneId: 'Asia/Vladivostok', returnComment: null, autoSubmitAt: null,
  enteredBy: null, marks: [], currentVersion: 1, versions: [], acceptWouldMark: false,
  lines: [{ id: 'l1', workTypeId: 'w1', variantId: null, unitId: 'u1', volume: 5, sectionId: null, location: null,
    comment: null, details: {}, isCrew: false, materials: [], participants: [], crewRole: null,
    photos: [{ id: 'p1', state: 'pending' }] }],
};
const refusal = (status: number, data: object = {}) => ({ response: { status, data } });
let current: WorkReport;

beforeEach(() => {
  vi.resetAllMocks();
  current = report;
  vi.mocked(apiClient.get).mockImplementation(async path => ({ data: path === '/work/sites/mine' ? sites : current }));
});
afterEach(cleanup);

function mount(id: string) {
  render(<QueryClientProvider client={new QueryClient()}>
    <MemoryRouter initialEntries={[`/my-reports/${id}`]}>
      <Routes><Route path="/my-reports/:reportId" element={<ReportForm mode="own" />} /></Routes>
    </MemoryRouter>
  </QueryClientProvider>);
}

it('при отказе 409 сохраняет выбранную смену и после перечитывания отправляет свежую версию и строки', async () => {
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  fireEvent.click(await screen.findByRole('tab', { name: 'Ночь' }));
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  expect((await screen.findByRole('alert')).textContent).toContain('Отчёт тем временем изменили');
  expect(screen.getByRole('tab', { name: 'Ночь' }).getAttribute('aria-selected')).toBe('true');
  current = { ...report, version: '2', lines: [{ ...report.lines[0], volume: 14 }] };
  fireEvent.click(screen.getByRole('button', { name: 'Перечитать' }));
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { ...current, shift: 'night', version: '3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/work/reports/r1',
    expect.objectContaining({ shift: 'night', changeId: expect.any(String), lines: [expect.objectContaining({ volume: 14 })] }),
    { headers: { 'If-Match': '2' } }));
  const body = vi.mocked(apiClient.put).mock.lastCall?.[1] as { lines: unknown[] };
  expect(body.lines[0]).not.toHaveProperty('photos');
});

it('использует предложенную ночную смену, показывает границу и даёт открыть совпавший отчёт', async () => {
  vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(409, { existingId: 'r1' }));
  mount('new');
  expect(await screen.findByText('ваши стройки (2)')).toBeTruthy();
  expect(screen.getByRole('tab', { name: 'Ночь' }).getAttribute('aria-selected')).toBe('true');
  await waitFor(() => expect((screen.getByRole('button', { name: 'Создать черновик' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Создать черновик' }));
  expect(await screen.findByText('На эту смену отчёт уже есть.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenCalledWith('/work/reports', expect.objectContaining({
    constructionId: 'site-a', shift: 'night', date: '2026-10-31',
  }));
  fireEvent.click(screen.getByRole('button', { name: 'Открыть' }));
  expect(await screen.findByRole('heading', { name: /31\.10\.2026 · день/ })).toBeTruthy();
});
