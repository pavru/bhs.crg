// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { SummaryRequest, SummaryResponse } from '@/shared/api/workSummary';
import { NARROW } from '@/shared/hooks/useMedia';
import { SummaryPage } from './SummaryPage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

const days = ['2026-10-30', '2026-10-31', '2026-11-01'];
const row = {
  employeeId: 'employee', employeeName: 'Ирина Волкова', constructionId: 'site', constructionName: 'Терминал',
  timeZoneId: 'Asia/Vladivostok', contact: { state: 'not-tracked' },
  cells: [
    [{ state: 'report', shift: 'night', reportId: 'night-id', status: 'submitted', hours: 7 }],
    [{ state: 'report', shift: 'day', reportId: 'day-id', status: 'accepted', hours: 8 },
      { state: 'draft', shift: 'night', autoSubmitAt: '2026-11-01T14:00:00Z' }],
    [{ state: 'future', shift: 'day' }],
  ],
} satisfies SummaryResponse['rows'][number];
let answer: SummaryResponse;
let narrow: boolean;

function mount(search = '') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>
    <MemoryRouter initialEntries={[`/work-summary${search}`]}><Routes>
      <Route path="work-summary" element={<SummaryPage />} />
      <Route path="work-review/:reportId" element={<h1>Карточка Work-21</h1>} />
    </Routes></MemoryRouter>
  </QueryClientProvider>);
}

function params() {
  return vi.mocked(apiClient.get).mock.calls.filter(([url]) => url === '/work/summary')
    .map(([, config]) => config?.params as SummaryRequest).at(-1);
}

beforeEach(() => {
  narrow = false;
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === NARROW && narrow, addEventListener() {}, removeEventListener() {},
  }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  answer = {
    days, rows: [row], constructions: [{ id: row.constructionId, name: row.constructionName }],
    boundary: 'отчёты ваших строек (1)', boundaryOutcome: 'sites', reason: null, total: 1, offset: 0, limit: 100,
  };
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async url => {
    if (url === '/work/summary') return { data: answer };
    if (url === '/periods') return { data: { today: '2026-10-31' } };
    if (url === '/account/access') return { data: {
      permissions: ['work.report.review'], modules: [{ code: 'work', available: true }],
    } };
    throw new Error(`Неожиданный адрес ${url}`);
  });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('сводка Work-23', () => {
  it('берёт колонки из ответа и показывает одну ночную отдельно от клетки с двумя сменами', async () => {
    mount();
    const table = await screen.findByRole('table');
    expect(within(table).getAllByRole('columnheader')).toHaveLength(5);
    const reportRow = within(table).getByRole('row', { name: /Ирина Волкова/ });
    const [, night, two] = within(reportRow).getAllByRole('cell');
    expect(within(night).getAllByRole('link')).toHaveLength(1);
    expect(within(night).getByText('☾')).toBeTruthy();
    expect(within(night).queryByRole('button')).toBeNull();
    expect(within(two).getByRole('link')).toBeTruthy();
    expect(within(two).getByRole('button', { name: /Черновик/ })).toBeTruthy();
    expect(within(two).getByText('☾')).toBeTruthy();
  });

  it('черновик показывает время стройки, отчёт открывается в карточке Work-21', async () => {
    mount();
    const table = await screen.findByRole('table');
    fireEvent.click(within(table).getByRole('button', { name: /Черновик/ }));
    expect((await screen.findByRole('tooltip')).textContent).toContain('по времени стройки');
    expect(screen.queryByRole('heading', { name: 'Карточка Work-21' })).toBeNull();
    fireEvent.click(within(table).getByRole('link', { name: /Ночная смена.*7 ч/ }));
    expect(await screen.findByRole('heading', { name: 'Карточка Work-21' })).toBeTruthy();
  });

  it('передаёт стройку и даты в запрос и называет причину пустого охвата', async () => {
    answer = { ...answer, rows: [], constructions: [], reason: 'not-assigned', boundaryOutcome: 'not-assigned',
      boundary: 'отчёты ваших строек (0)', total: 0 };
    mount('?construction=site&from=2026-10-30&to=2026-11-01');
    expect(await screen.findByText('Вы не назначены ни на одну стройку')).toBeTruthy();
    expect(screen.getByText('отчёты ваших строек (0)')).toBeTruthy();
    expect(params()).toEqual({ construction: 'site', from: days[0], to: days[2], offset: 0 });
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('узкий экран показывает один день и даёт перелистать период без нового запроса', async () => {
    narrow = true;
    mount();
    const table = await screen.findByRole('table');
    expect(within(table).getAllByRole('columnheader')).toHaveLength(3);
    const requested = params();
    fireEvent.click(screen.getByRole('button', { name: 'Предыдущий день' }));
    await waitFor(() => expect(within(table).getByRole('link', { name: /7 ч/ })).toBeTruthy());
    expect(within(table).queryByRole('button', { name: /Черновик/ })).toBeNull();
    expect(params()).toBe(requested);
  });

  it('стрелки выбирают клетки без отчётов, а в Tab остаётся только выбранная клетка и её действия', async () => {
    answer.rows.push({ ...row, employeeId: 'other', employeeName: 'Денис Белов', cells: [
      [{ state: 'not-assigned', shift: 'day' }], [{ state: 'not-received', shift: 'day' }], [{ state: 'future', shift: 'day' }],
    ] });
    mount();
    const table = await screen.findByRole('table');
    const cells = within(table).getAllByRole('group');
    const details = screen.getByRole('region', { name: 'Подробности клетки' });

    expect(cells.filter(cell => cell.tabIndex === 0)).toEqual([cells[0]]);
    act(() => cells[0].focus());
    fireEvent.keyDown(cells[0], { key: 'ArrowDown' });
    expect(document.activeElement).toBe(cells[3]);
    expect(within(details).getByText(/Денис Белов/)).toBeTruthy();
    expect(within(details).getByText(/Не назначен/)).toBeTruthy();

    fireEvent.keyDown(cells[3], { key: 'ArrowRight' });
    expect(document.activeElement).toBe(cells[4]);
    expect(within(details).getByText(/Не поступал/)).toBeTruthy();
    expect(cells.filter(cell => cell.tabIndex === 0)).toEqual([cells[4]]);
    expect(within(table).getAllByRole('link').every(link => link.tabIndex === -1)).toBe(true);
  });
});
