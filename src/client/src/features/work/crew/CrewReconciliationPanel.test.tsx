// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { apiClient } from '@/shared/api/client';
import { WORK_REVIEW_REPORT_PERMISSIONS, type CrewGroup, type CrewGroupsPage } from '@/shared/api/work';
import { setFormatLocale } from '@/shared/format/format';
import { RequireAccess } from '@/shared/ui/ProtectedRoute';
import { ReviewQueuePage } from '../review/ReviewQueuePage';
import { ReportCard } from '../review/ReportCard';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: {} }) }));

const group: CrewGroup = {
  id: 'group-1', constructionId: 'site-1', date: '2026-11-21', shift: 'night', workTypeId: 'type-1',
  version: '10', state: 'pending', mismatch: true, total: null, unitId: null, approvedBy: null,
  approvedByName: null, approvedAt: null, unsubmitted: 2, actions: ['approve'],
  lines: [
    { id: 'line-1', reportId: 'report-1', employeeId: 'employee-1', fullName: 'Иван Крылов',
      variantId: 'variant-1', volume: 120, unitId: 'm', crewRole: 'lead' },
    { id: 'line-2', reportId: 'report-2', employeeId: 'employee-2', fullName: 'Ирина Березина',
      variantId: null, volume: 100, unitId: 'm', crewRole: 'member' },
  ],
};

let crewPage: CrewGroupsPage;
let permissions: string[];

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});

beforeEach(() => {
  permissions = ['work.report.read', 'work.crew.approve'];
  crewPage = {
    items: [group], constructions: [{ id: 'site-1', name: 'Терминал' }],
    workTypes: { 'type-1': 'Прокладка кабеля' }, workVariants: { 'variant-1': 'В лотке' }, units: { m: 'м' },
  };
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.post).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async path => {
    if (path === '/work/crew-groups') return { data: crewPage };
    if (path === '/account/access') return { data: { permissions, modules: [{ code: 'work', available: true }] } };
    if (path === '/work/review/queue') return { data: { items: [], limit: 50, offset: 0, total: 0, counts: {},
      boundary: { code: 'all', text: 'все стройки', sites: null }, reason: 'nothing-pending' } };
    throw new Error(`Неожиданный адрес ${path}`);
  });
});
afterEach(() => {
  cleanup();
  setFormatLocale('ru-RU');
});

function mount(search = '?tab=crew', report = false) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[`/work-review${report ? '/report-1' : ''}${search}`]}>
      <Routes>
        <Route element={<RequireAccess />}>
          <Route path="work-review" element={<ReviewQueuePage />} />
        </Route>
        <Route element={<RequireAccess additionalPermission={[...WORK_REVIEW_REPORT_PERMISSIONS]} />}>
          <Route path="work-review/:reportId" element={<ReportCard />} />
        </Route>
      </Routes>
    </MemoryRouter>
  </QueryClientProvider>);
}

async function openApproval() {
  fireEvent.click(await screen.findByRole('button', { name: 'Утвердить итог' }));
  return screen.findByRole('dialog', { name: 'Утвердить итог бригады' });
}

async function choose(dialog: HTMLElement, option: string | RegExp) {
  fireEvent.keyDown(within(dialog).getByRole('combobox', { name: 'Источник итогового объёма' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

describe('сверка бригад', () => {
  it('открывается вкладкой, берёт стройку и период из своего отбора, сохраняет отбор очереди', async () => {
    mount('?employeeId=employee-1&offset=50&crewConstructionId=site-1&crewFrom=2026-11-20&crewTo=2026-11-21');
    await screen.findByText('Ждущих решения нет');
    fireEvent.click(screen.getByRole('tab', { name: 'Сверка бригад' }));
    expect(await screen.findByRole('heading', { name: 'Прокладка кабеля' })).toBeTruthy();
    expect(apiClient.get).toHaveBeenCalledWith('/work/crew-groups', expect.objectContaining({
      params: { constructionId: 'site-1', from: '2026-11-20', to: '2026-11-21' },
    }));
    const link = screen.getByRole('link', { name: 'Открыть отчёт · Иван Крылов' });
    expect(link.getAttribute('href')).toContain('tab=crew');
    expect(link.getAttribute('href')).toContain('employeeId=employee-1');
    fireEvent.click(screen.getByRole('tab', { name: 'Отчёты' }));
    expect(await screen.findByText('Ждущих решения нет')).toBeTruthy();
    expect(apiClient.get).toHaveBeenCalledWith('/work/review/queue', expect.objectContaining({
      params: expect.objectContaining({ employeeId: 'employee-1', offset: 50 }),
    }));
  });

  it('показывает прежний итог и расхождение из ответа, скрывает действие без approve', async () => {
    permissions = ['work.report.read'];
    crewPage.items = [{ ...group, state: 'stale', total: 120, unitId: 'm', actions: [],
      approvedByName: 'Менеджер', approvedAt: '2026-11-21T10:15:00Z' }];
    mount();
    expect(await screen.findByText('Требует решения')).toBeTruthy();
    expect(screen.getByText('Есть расхождение в объёмах')).toBeTruthy();
    expect(screen.getByText(/Прежний итог · 120 м · Менеджер/)).toBeTruthy();
    expect(screen.getByText('Не подали отчёт · 2')).toBeTruthy();
    expect(screen.getByText('В лотке')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Утвердить итог' })).toBeNull();
  });

  it.each(['', '?tab=crew'])('право утверждения открывает только сверку по адресу /work-review%s', async search => {
    permissions = ['work.crew.approve'];
    mount(search);
    expect(await screen.findByRole('heading', { name: 'Прокладка кабеля' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Сверка бригад' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('button', { name: 'Утвердить итог' })).toBeTruthy();
    expect(screen.queryByRole('tab', { name: 'Отчёты' })).toBeNull();
    expect(screen.queryByRole('link', { name: /Открыть отчёт/ })).toBeNull();
    expect(vi.mocked(apiClient.get).mock.calls.map(([url]) => url)).not.toContain('/work/review/queue');
  });

  it('право утверждения не открывает карточку по прямой ссылке', async () => {
    permissions = ['work.crew.approve'];
    mount('?tab=crew', true);
    expect(await screen.findByRole('heading', { name: 'Раздел «Приёмка» недоступен' })).toBeTruthy();
    expect(screen.getByText('work.report.review')).toBeTruthy();
    expect(screen.getByText('work.report.read')).toBeTruthy();
    expect(vi.mocked(apiClient.get).mock.calls.map(([url]) => url)).not.toContain('/work/review/reports/report-1');
  });

  it.each([
    ['объём строки', /120 м · Иван Крылов/, 120],
    ['свой объём', 'Указать свой объём', 112.3456],
  ] as const)('утверждает %s с версией открытой группы', async (_kind, option, total) => {
    vi.mocked(apiClient.post).mockImplementation(async () => {
      const result: CrewGroup = { ...group, version: '11', total, unitId: 'm', state: 'approved', actions: [],
        approvedByName: 'Менеджер', approvedAt: '2026-11-21T10:15:00Z' };
      crewPage.items = [result];
      return { data: result };
    });
    mount();
    const dialog = await openApproval();
    await choose(dialog, option);
    if (option === 'Указать свой объём') fireEvent.change(within(dialog).getByRole('textbox', { name: 'Свой объём, м' }),
      { target: { value: '112,3456' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Утвердить итог' }));
    expect(await screen.findByText('Утверждено')).toBeTruthy();
    expect(apiClient.post).toHaveBeenCalledWith('/work/crew-groups/group-1/approve', { total }, { headers: { 'If-Match': '10' } });
    expect(screen.getByText(/Итог · .* м · Менеджер/)).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('читает вставленный объём с разделителем тысяч в локали отображения', async () => {
    setFormatLocale('en-US');
    crewPage.items = [{ ...group, lines: [{ ...group.lines[0], volume: 1234 }, group.lines[1]] }];
    vi.mocked(apiClient.post).mockResolvedValue({
      data: { ...group, version: '11', total: 1234, unitId: 'm', state: 'approved', actions: [] },
    });
    mount();
    const volume = (await screen.findByRole('cell', { name: '1,234' })).textContent;
    const dialog = await openApproval();
    await choose(dialog, 'Указать свой объём');
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Свой объём, м' }), { target: { value: volume } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Утвердить итог' }));
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith(
      '/work/crew-groups/group-1/approve', { total: 1234 }, { headers: { 'If-Match': '10' } },
    ));
  });

  it('после отказа по версии перечитывает список и даёт принять решение заново', async () => {
    vi.mocked(apiClient.post).mockRejectedValueOnce({
      response: { status: 409, data: { error: 'Группу тем временем изменили.' } },
    });
    mount();
    const dialog = await openApproval();
    await choose(dialog, /120 м · Иван Крылов/);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Утвердить итог' }));
    expect(await within(dialog).findByText('Группу тем временем изменили.')).toBeTruthy();
    crewPage.items = [{ ...group, version: '12', mismatch: false }];
    fireEvent.click(within(dialog).getByRole('button', { name: 'Перечитать группу' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.queryByText('Есть расхождение в объёмах')).toBeNull();
    expect(screen.getByRole('button', { name: 'Утвердить итог' })).toBeTruthy();
    expect(apiClient.post).toHaveBeenCalledTimes(1);
  });
});
