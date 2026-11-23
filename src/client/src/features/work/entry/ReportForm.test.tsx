// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { reportQuery, type EntrySites, type ReportMode, type WorkReport } from '@/shared/api/workReports';
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

function mount(id: string, mode: ReportMode = 'own', client = new QueryClient()) {
  const path = mode === 'own' ? '/my-reports' : '/work-review/entry';
  render(<QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[`${path}/${id}`]}>
      <Routes><Route path={`${path}/:reportId`} element={<ReportForm mode={mode} />} /></Routes>
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

it('перечитывает ещё свежую карточку из кэша и разблокирует возвращённый отчёт', async () => {
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: 30_000 } } });
  client.setQueryData(reportQuery('own', report.id).queryKey, { ...report, status: 'submitted' });
  current = { ...report, status: 'returned', version: '2', returnComment: 'Уточните дату смены.' };
  mount(report.id, 'own', client);
  expect(screen.getByText('Подан')).toBeTruthy();
  expect((screen.getByRole('tab', { name: 'Ночь' }) as HTMLButtonElement).disabled).toBe(true);
  expect(await screen.findByText('Уточните дату смены.')).toBeTruthy();
  expect((screen.getByRole('tab', { name: 'Ночь' }) as HTMLButtonElement).disabled).toBe(false);
});

it('фоновое чтение не меняет основу If-Match, пока есть локальная правка', async () => {
  const client = new QueryClient();
  mount(report.id, 'own', client);
  fireEvent.click(await screen.findByRole('tab', { name: 'Ночь' }));
  current = { ...report, status: 'returned', version: '2' };
  await act(() => client.refetchQueries({ queryKey: reportQuery('own', report.id).queryKey }));
  expect(screen.getByRole('tab', { name: 'Ночь' }).getAttribute('aria-selected')).toBe('true');
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  expect((await screen.findByRole('alert')).textContent).toContain('Отчёт тем временем изменили');
  expect(apiClient.put).toHaveBeenCalledWith('/work/reports/r1', expect.objectContaining({ shift: 'night' }),
    { headers: { 'If-Match': '1' } });
});

it('подбирает сотрудника и его стройки на дату смены и переносит эту дату в новый отчёт', async () => {
  const date = '2026-10-29';
  vi.mocked(apiClient.get).mockImplementation(async (path, config) => {
    if (path === '/work/entry/reports/candidates') {
      const items = config?.params.date === date
        ? [{ employeeId: 'e1', name: 'Петров', eligible: true, personnelNumber: null,
          reason: null, assigned: null, account: { state: 'none' } }] : [];
      return { data: { items, more: false, limit: 100 } };
    }
    return { data: sites };
  });
  vi.mocked(apiClient.post).mockRejectedValueOnce(new Error('Запись недоступна'));
  mount('new', 'entry');
  const field = within(screen.getByRole('group', { name: 'Дата смены' }));
  expect(screen.queryByRole('checkbox', { name: 'Отметить: Петров' })).toBeNull();
  fireEvent.paste(field.getByRole('textbox', { name: 'day' }), { clipboardData: { getData: () => date } });
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Отметить: Петров' }));
  fireEvent.click(screen.getByRole('button', { name: 'Продолжить' }));
  await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/work/entry/reports/candidates/e1/sites',
    expect.objectContaining({ params: { date } })));
  fireEvent.click(await screen.findByRole('button', { name: 'Создать черновик' }));
  await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/work/entry/reports', expect.objectContaining({
    employeeId: 'e1', constructionId: 'site-a', date, shift: 'night',
  })));
});

it('после потерянного ответа повторяет создание с тем же ID и сохраняет дату и смену отдельной правкой', async () => {
  vi.mocked(apiClient.post).mockRejectedValueOnce(new Error('Ответ потерян'));
  mount('new');
  await waitFor(() => expect((screen.getByRole('button', { name: 'Создать черновик' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Создать черновик' }));
  await screen.findByRole('alert');
  const attempt = vi.mocked(apiClient.post).mock.lastCall?.[1] as { id: string };
  current = { ...report, id: attempt.id, shift: 'night' };
  vi.mocked(apiClient.post).mockResolvedValueOnce({ data: current });
  const date = '2026-10-30';
  const field = within(screen.getByRole('group', { name: 'Дата смены' }));
  fireEvent.paste(field.getByRole('textbox', { name: 'day' }), { clipboardData: { getData: () => date } });
  fireEvent.click(screen.getByRole('tab', { name: 'День' }));
  await waitFor(() => expect((screen.getByRole('button', { name: 'Создать черновик' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Создать черновик' }));
  expect(await screen.findByText('Черновик уже создан. Новые значения ещё не сохранены.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenLastCalledWith('/work/reports', expect.objectContaining({ id: attempt.id, date, shift: 'day' }));
  expect(screen.getByRole('tab', { name: 'День' }).getAttribute('aria-selected')).toBe('true');
  expect((screen.getByRole('button', { name: 'Подать отчёт' }) as HTMLButtonElement).disabled).toBe(true);
  vi.mocked(apiClient.put).mockImplementationOnce(async () => {
    current = { ...current, date, shift: 'day', version: '2' };
    return { data: current };
  });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith(`/work/reports/${attempt.id}`,
    expect.objectContaining({ date, shift: 'day' }), { headers: { 'If-Match': '1' } }));
  expect(await screen.findByRole('heading', { name: /30\.10\.2026 · день/ })).toBeTruthy();
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
