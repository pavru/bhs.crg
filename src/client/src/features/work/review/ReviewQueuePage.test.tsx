// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router';
import { apiClient } from '@/shared/api/client';
import type { ReviewQueuePage as QueueData, ReviewQueueRow } from '@/shared/api/work';
import type { ReviewQueueRequest } from '@/shared/api/workReview';
import { ReviewQueuePage } from './ReviewQueuePage';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

const row: ReviewQueueRow = {
  id: 'report-1', constructionId: 'site-1', employeeId: 'employee-1', date: '2026-11-21', shift: 'night',
  hours: 10, status: 'submitted', version: '1', submittedAt: '2026-11-22T08:00:00Z', submittedBy: 'manual',
  enteredBy: null, lines: 1, marks: [{ code: 'photos-pending', text: 'фото не загружены: 2' }],
  actions: ['accept', 'return', 'edit'], acceptWouldMark: false, photosPending: 2,
};

let answer: (params: ReviewQueueRequest) => QueueData | Promise<QueueData>;
let permissions: string[];

function page(override: Partial<QueueData> = {}): QueueData {
  return {
    items: [row], total: 1, offset: 0, limit: 50, counts: { 'photos-pending': 1, 'late-delivery': 0 },
    employees: [{ id: row.employeeId, name: 'Максим Крылов' }],
    boundary: { code: 'sites', text: 'отчёты ваших строек (2)', sites: 2 }, reason: null, ...override,
  };
}

function TestCard() {
  const navigate = useNavigate();
  return <button type="button" onClick={() => navigate(-1)}>Вернуться в очередь</button>;
}

function mount(search = '') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>
    <MemoryRouter initialEntries={[`/work-review${search}`]}><Routes>
      <Route path="work-review" element={<ReviewQueuePage />} />
      <Route path="work-review/:reportId" element={<TestCard />} />
    </Routes></MemoryRouter>
  </QueryClientProvider>);
}

function queueCalls() {
  return vi.mocked(apiClient.get).mock.calls
    .filter(([url]) => url === '/work/review/queue')
    .map(([, config]) => config?.params as ReviewQueueRequest);
}

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  answer = () => page();
  permissions = ['work.report.review', 'core.employees.read'];
  vi.mocked(apiClient.get).mockReset();
  vi.mocked(apiClient.get).mockImplementation(async (url, config) => {
    if (url === '/work/review/queue') return { data: await answer(config?.params as ReviewQueueRequest) };
    if (url === '/work/sites/mine') return { data: { items: [
      { id: 'site-1', name: 'Терминал', kinds: ['manager'] },
      { id: 'site-2', name: 'Чужое назначение монтажником', kinds: ['installer'] },
    ] } };
    if (url === '/employees') return { data: [{ id: 'employee-1', displayName: 'Максим Крылов' }] };
    if (url === '/account/access') return { data: { permissions, modules: [] } };
    throw new Error(`Неожиданный адрес ${url}`);
  });
});
afterEach(cleanup);

describe('очередь приёмки', () => {
  it('при возврате из карточки восстанавливает прокрутку очереди с прежним отбором', async () => {
    const { container } = mount('?constructionId=site-1&offset=50');
    const report = await screen.findByRole('link', { name: /Открыть отчёт/ });
    const before = container.firstElementChild as HTMLDivElement;
    before.scrollTop = 624;
    fireEvent.scroll(before);
    fireEvent.click(report);
    fireEvent.click(await screen.findByRole('button', { name: 'Вернуться в очередь' }));
    expect(await screen.findByRole('link', { name: /Открыть отчёт/ })).toBeTruthy();
    expect(container.firstElementChild).not.toBe(before);
    expect(container.firstElementChild?.scrollTop).toBe(624);
    expect(queueCalls().at(-1)).toMatchObject({ constructionId: 'site-1', offset: 50 });
  });

  it('показывает границу, ночную смену и текст пометки из ответа, нулевой чип скрыт', async () => {
    mount();
    expect(await screen.findByText('отчёты ваших строек (2)')).toBeTruthy();
    const report = await screen.findByRole('row', { name: /Максим Крылов/ });
    expect(await within(report).findByText('Терминал')).toBeTruthy();
    expect(within(report).getByText('☾ ночь')).toBeTruthy();
    expect(within(report).getByText('фото не загружены: 2')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /поздняя доставка/ })).toBeNull();
  });

  it('показывает имена в очереди для права чтения без доступа к справочнику сотрудников', async () => {
    permissions = ['work.report.read'];
    answer = () => page({
      employees: [{ id: row.employeeId, name: 'Максим Крылов' }, { id: 'employee-2', name: 'Ирина Сафонова' }],
      boundary: { code: 'all', text: 'все стройки', sites: null },
    });
    mount('?employeeId=employee-2');
    expect(await screen.findByRole('row', { name: /Максим Крылов/ })).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Сотрудник' }).textContent).toContain('Ирина Сафонова');
    expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url === '/employees')).toBe(false);
  });

  it.each([
    ['пометка', /фото не загружены.*1/, { mark: 'photos-pending', offset: 0 }],
    ['пагинация', 'Следующая страница', { offset: 50 }],
  ] as const)('сохраняет фокус при загрузке (%s)', async (_kind, name, nextRequest) => {
    let resolve: (data: QueueData) => void = () => {};
    const pending = new Promise<QueueData>(done => { resolve = done; });
    answer = params => params.mark || params.offset ? pending : page({ total: 51 });
    mount();
    const control = await screen.findByRole('button', { name });
    act(() => control.focus());
    fireEvent.click(control);
    await screen.findByText('Загрузка очереди приёмки…');
    expect(queueCalls().at(-1)).toMatchObject(nextRequest);
    expect(document.activeElement).toBe(control);
    if (nextRequest.offset > 0) {
      expect(control.getAttribute('aria-disabled')).toBe('true');
      fireEvent.click(control);
      expect(queueCalls()).toHaveLength(2);
    }
    await act(async () => {
      resolve(page({
        items: [{ ...row, hours: 9 }],
        offset: nextRequest.offset, total: nextRequest.offset + 1,
      }));
    });
    expect(await screen.findByRole('cell', { name: '9' })).toBeTruthy();
    expect(document.activeElement).toBe(control);
  });

  it('запрашивает выбранную пометку и сбрасывает сдвиг страницы', async () => {
    answer = () => page({ total: 51, offset: 50 });
    mount('?constructionId=site-1&from=2026-11-20&offset=50');
    fireEvent.click(await screen.findByRole('button', { name: /фото не загружены.*1/ }));
    await waitFor(() => expect(queueCalls().at(-1)).toMatchObject({
      constructionId: 'site-1', from: '2026-11-20', mark: 'photos-pending', offset: 0,
    }));
  });

  it('отбирает текущие пропуски отдельным чипом и снимает чип при нулевом счётчике', async () => {
    const customer = { code: 'fields-missing', text: 'не заполнены поля: 1' };
    answer = ({ mark }) => mark === 'required-missing'
      ? page({ items: [], reason: 'filtered-out', total: 0, counts: { 'required-missing': 0, 'fields-missing': 1 } })
      : page({ items: [{ ...row, marks: [{ code: 'required-missing', text: 'не указаны обязательные материалы' }, customer] }],
        counts: { 'required-missing': 1, 'fields-missing': 1 } });
    mount('?constructionId=site-1');
    expect(await screen.findByText('не указаны обязательные материалы')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /не заполнены место или материалы.*1/ }));
    expect(await screen.findByText('Отбор скрыл все отчёты')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /не заполнены место или материалы/ })).toBeNull();
    expect(screen.getByRole('button', { name: /не заполнены поля.*1/ })).toBeTruthy();
    expect(queueCalls().at(-1)).toMatchObject({ constructionId: 'site-1', mark: 'required-missing', offset: 0 });
  });

  it('показывает незнакомую пометку строки словами сервера', async () => {
    answer = () => page({ items: [{ ...row, marks: [{ code: 'future', text: 'Проверьте допуск' }] }], counts: { future: 2 } });
    mount();
    expect(within(await screen.findByRole('row', { name: /Проверьте допуск/ })).getByText('Проверьте допуск')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Проверьте допуск.*2/ })).toBeTruthy();
    expect(queueCalls().some(params => params.limit === 1)).toBe(false);
  });

  it('получает текст нового кода за пределами страницы под тем же отбором', async () => {
    answer = ({ limit }) => limit === 1
      ? page({ items: [{ ...row, marks: [{ code: 'future', text: 'Нужна сверка допуска' }] }] })
      : page({ counts: { 'photos-pending': 1, future: 3 } });
    mount('?constructionId=site-1&employeeId=employee-1&from=2026-11-21&mark=photos-pending');
    expect(await screen.findByRole('button', { name: /Нужна сверка допуска.*3/ })).toBeTruthy();
    expect(queueCalls()).toContainEqual({
      constructionId: 'site-1', employeeId: 'employee-1', from: '2026-11-21', to: undefined, mark: 'future', limit: 1,
    });
  });

  it.each([
    ['not-assigned', 'Вы не назначены ни на одну стройку'],
    ['nothing-pending', 'Ждущих решения нет'],
    ['not-linked', 'Учётная запись не связана с сотрудником'],
    ['directory-missing', 'Справочник сотрудников не заведён'],
  ] as const)('показывает причину пустоты %s', async (reason, title) => {
    answer = () => page({ reason, items: [], total: 0, counts: {} });
    mount();
    expect(await screen.findByText(title)).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сбросить отбор' })).toBeNull();
  });

  it('сбрасывает исключивший все отчёты отбор', async () => {
    answer = ({ employeeId }) => employeeId ? page({ reason: 'filtered-out', items: [], total: 0, counts: {} }) : page();
    mount('?employeeId=employee-2');
    expect(await screen.findByText('Отбор скрыл все отчёты')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сбросить отбор' }));
    expect(await screen.findByRole('row', { name: /Максим Крылов/ })).toBeTruthy();
    expect(queueCalls().at(-1)?.employeeId).toBeUndefined();
  });
});
