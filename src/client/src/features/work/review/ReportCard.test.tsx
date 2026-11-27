// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeAll, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { ReviewCardData, ReviewFullCard } from '@/shared/api/workReview';
import { WORK_SUMMARY_KEY } from '@/shared/api/workSummary';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { ReportCard } from './ReportCard';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));

const card: ReviewFullCard = {
  id: 'r1', employeeName: 'Ирина Иванова', constructionName: 'Терминал', timeZoneId: 'Asia/Vladivostok',
  actions: ['accept', 'return', 'edit'], status: 'submitted', viewedVersion: 1, atClosingVersion: null,
  currentRequiredMissing: [],
  workTypes: { w1: 'Прокладка кабеля' }, workVariants: {}, units: { m: 'м' }, sections: {},
  report: {
    id: 'r1', status: 'submitted', employeeId: 'e1', constructionId: 's1', date: '2026-11-23', shift: 'night',
    hours: 8, note: null, version: 'v1', marks: [], acceptWouldMark: true, currentVersion: 1, versions: [],
    lines: [{ id: 'l1', workTypeId: 'w1', variantId: null, volume: 12.5, unitId: 'm', sectionId: null,
      location: 'В щитовой', isCrew: false, details: { cond: 'Наружная' }, materials: [], participants: [],
      photos: [{ id: 'p1', state: 'pending' }], comment: null, crewRole: null }],
  },
};
let read: (number?: number) => ReviewCardData;
const refusal = (status: number, data: object) => ({ response: { status, data } });

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  vi.resetAllMocks();
  read = () => card;
  vi.mocked(apiClient.get).mockImplementation(async (path, config) => ({
    data: path === '/work/line-details-schema' ? { fields: [] }
      : path.endsWith('/journal') ? { items: [] } : read(config?.params?.number),
  }));
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

function mount(search = '') {
  const queryClient = new QueryClient();
  render(<QueryClientProvider client={queryClient}>
    <MemoryRouter initialEntries={[`/work-review/r1${search}`]}>
      <NavigationGuardProvider><Routes>
        <Route path="work-review/:reportId" element={<ReportCard />} />
        <Route path="work-review" element={<h1>Очередь</h1>} />
      </Routes></NavigationGuardProvider>
    </MemoryRouter>
  </QueryClientProvider>);
  return queryClient;
}

async function openDecision(label: string) {
  fireEvent.click(await screen.findByRole('button', { name: label }));
  return within(await screen.findByRole('dialog'));
}

it('показывает один текст для 404', async () => {
  read = () => { throw refusal(404, { error: 'Отчёт не найден' }); };
  mount();
  expect(await screen.findByText('Отчёт не найден: его нет, или он вне ваших строек')).toBeTruthy();
});

it('оставляет карточку с пустыми actions для чтения, с работами и недогруженным фото', async () => {
  read = () => ({ ...card, actions: [], report: { ...card.report, lines: [{ ...card.report.lines[0], volume: 0.0001 }] } });
  mount();
  expect(await screen.findByText('Прокладка кабеля')).toBeTruthy();
  expect(screen.getByText('фото не загружены: 1')).toBeTruthy();
  expect(screen.getByText('0,0001 м')).toBeTruthy();
  expect(screen.getByText(/☾ ночь.*8 ч/)).toBeTruthy();
  for (const name of ['Принять', 'Вернуть', 'Действия отчёта']) expect(screen.queryByRole('button', { name })).toBeNull();
});

it('называет незаполненные поля из строки ответа и оставляет доступной приёмку', async () => {
  read = () => ({ ...card, report: { ...card.report, lines: [{
    ...card.report.lines[0], details: { act: 'Локальное значение не задаёт пометку' },
    missingFields: [{ key: 'act', title: 'Номер акта' }],
  }] } });
  mount();
  expect(await screen.findByText('Номер акта')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Принять' })).toBeTruthy();
});

it('называет текущие пропуски и снимает пометку при перечитывании после сохранения', async () => {
  const customer = { code: 'fields-missing', text: 'не заполнены поля: 1' };
  read = () => ({ ...card, currentRequiredMissing: [{ line: 1, lineId: 'l1', field: 'location' }],
    report: { ...card.report, marks: [{ code: 'required-missing', text: 'не указано обязательное место работ' }, customer] } });
  vi.mocked(apiClient.put).mockImplementation(async () => {
    read = () => ({ ...card, report: { ...card.report, marks: [customer] } });
    return { data: { id: 'r1' } };
  });
  const client = mount();
  const queueKey = ['work', 'review-queue', { mark: 'required-missing' }];
  client.setQueryData(queueKey, {});
  expect(await screen.findByText('Строка 1 · Место работ')).toBeTruthy();
  expect(screen.getByText('не указано обязательное место работ')).toBeTruthy();
  fireEvent.keyDown(screen.getByRole('button', { name: 'Действия отчёта' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Править строки' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Место строки 1' }), { target: { value: 'Коридор' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить правку' }));
  await waitFor(() => expect(screen.queryByText('Строка 1 · Место работ')).toBeNull());
  expect(screen.queryByText('не указано обязательное место работ')).toBeNull();
  expect(screen.getByText('не заполнены поля: 1')).toBeTruthy();
  expect(client.getQueryState(queueKey)?.isInvalidated).toBe(true);
});

it('перечитывает открытый черновик после автоподачи и останавливает опрос после подачи', async () => {
  vi.useFakeTimers();
  read = () => ({ id: card.id, employeeName: card.employeeName, constructionName: card.constructionName,
    actions: [], timeZoneId: card.timeZoneId, status: 'draft', autoSubmitAt: '2026-11-22T14:00:00Z' });
  mount();
  await act(async () => { await vi.advanceTimersByTimeAsync(20); });
  expect(screen.getByText('Черновик')).toBeTruthy();
  read = () => card;
  await act(async () => { await vi.advanceTimersByTimeAsync(15_020); });
  expect(screen.getByText('Прокладка кабеля')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Принять' })).toBeTruthy();
  const cardRequests = () => vi.mocked(apiClient.get).mock.calls.filter(([path]) => path === '/work/review/reports/r1').length;
  expect(cardRequests()).toBe(2);
  await act(async () => { await vi.advanceTimersByTimeAsync(30_000); });
  expect(cardRequests()).toBe(2);
});

it('показывает время автоподачи черновика по часам стройки и не запрашивает журнал', async () => {
  read = () => ({ id: card.id, employeeName: card.employeeName, constructionName: card.constructionName,
    actions: [], timeZoneId: card.timeZoneId, status: 'draft', autoSubmitAt: '2026-11-22T14:00:00Z' });
  mount();
  expect(await screen.findByText(/Автоподача.*00:00.*по времени стройки/)).toBeTruthy();
  expect(screen.queryByRole('table')).toBeNull();
  expect(vi.mocked(apiClient.get).mock.calls.some(([path]) => path.endsWith('/journal'))).toBe(false);
});

it('показывает предупреждение приёмки, отправляет If-Match и предлагает перечитать устаревшую версию', async () => {
  vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили, и эта правка не записана.' }));
  mount();
  const dialog = await openDecision('Принять');
  expect(dialog.getByText('В отчёте появится пометка «ввёл и принял один человек».')).toBeTruthy();
  fireEvent.click(dialog.getByRole('button', { name: 'Принять' }));
  expect(await dialog.findByText(/Отчёт тем временем изменили.*Перечитайте/)).toBeTruthy();
  expect(apiClient.post).toHaveBeenCalledWith('/work/reports/r1/accept', { changeId: expect.any(String) }, { headers: { 'If-Match': 'v1' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Перечитать отчёт' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
});

it('требует комментарий и перечитывает карточку после отдельного отказа по статусу', async () => {
  vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(409, { code: 'report-status', status: 'accepted' }));
  mount();
  const dialog = await openDecision('Вернуть');
  const send = dialog.getByRole<HTMLButtonElement>('button', { name: 'Вернуть' });
  expect(send.disabled).toBe(true);
  fireEvent.change(dialog.getByRole('textbox', { name: 'Что исправить' }), { target: { value: '  Уточните место.  ' } });
  fireEvent.click(send);
  expect(await dialog.findByText('Отчёт уже принят.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenCalledWith('/work/reports/r1/return', { changeId: expect.any(String), comment: 'Уточните место.' },
    { headers: { 'If-Match': 'v1' } });
  read = () => ({ ...card, status: 'accepted', actions: [] });
  fireEvent.click(dialog.getByRole('button', { name: 'Закрыть' }));
  expect(await screen.findByText('Принят', { exact: true })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Принять' })).toBeNull();
});

it.each([
  ['Принять', 'accepted'], ['Вернуть', 'returned'],
] as const)('после решения «%s» обновляет выборки факта по стройке', async (label, status) => {
  vi.mocked(apiClient.post).mockResolvedValue({ data: { id: card.id, status, version: 'v2' } });
  const queryClient = mount();
  const summary = [...WORK_SUMMARY_KEY, { from: '2026-11-23', to: '2026-11-23', construction: 's1', offset: 0 }];
  const affected = [
    summary, ['construction-progress', 's1', null], ['construction-progress', 's1', 'section'],
    ['planning', 'schedules', 's1', 'graph'],
  ];
  const other = ['construction-progress', 's2', null];
  for (const key of [...affected, other]) queryClient.setQueryData(key, {});
  const dialog = await openDecision(label);
  if (label === 'Вернуть') fireEvent.change(dialog.getByRole('textbox', { name: 'Что исправить' }), { target: { value: 'Уточните место' } });
  fireEvent.click(dialog.getByRole('button', { name: label }));
  await waitFor(() => {
    for (const key of affected) expect(queryClient.getQueryState(key)?.isInvalidated).toBe(true);
  });
  expect(queryClient.getQueryState(other)?.isInvalidated).toBe(false);
});

it('правит объём с версией чтения и сохраняет прочие поля строки и ссылки на фото', async () => {
  vi.mocked(apiClient.put).mockImplementation(async () => {
    read = () => ({ ...card, report: { ...card.report, marks: [{ code: 'manager-edit', text: 'правка менеджером' }] } });
    return { data: { id: 'r1' } };
  });
  mount();
  fireEvent.keyDown(await screen.findByRole('button', { name: 'Действия отчёта' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Править строки' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Объём строки 1' }), { target: { value: '15' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить правку' }));
  expect(await screen.findByText('правка менеджером')).toBeTruthy();
  expect(apiClient.put).toHaveBeenCalledWith('/work/entry/reports/r1', expect.objectContaining({
    changeId: expect.any(String), lines: [expect.objectContaining({ id: 'l1', volume: 15, details: { cond: 'Наружная' } })],
  }), { headers: { 'If-Match': 'v1' } });
  expect(vi.mocked(apiClient.put).mock.calls[0][1]).toMatchObject({ lines: [expect.not.objectContaining({ photos: expect.anything() })] });
});

it('перед уходом с правками даёт остаться, затем уйти без сохранения', async () => {
  mount('?constructionId=s1');
  fireEvent.keyDown(await screen.findByRole('button', { name: 'Действия отчёта' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('menuitem', { name: 'Править строки' }));
  const location = screen.getByRole<HTMLInputElement>('textbox', { name: 'Место строки 1' });
  fireEvent.change(location, { target: { value: 'На втором этаже' } });
  const back = screen.getByRole('button', { name: 'К очереди приёмки' });
  fireEvent.click(back);
  let dialog = within(await screen.findByRole('dialog', { name: 'Несохранённые изменения' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Отмена' }));
  expect(location.value).toBe('На втором этаже');
  fireEvent.click(back);
  dialog = within(await screen.findByRole('dialog', { name: 'Несохранённые изменения' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Не сохранять' }));
  expect(await screen.findByRole('heading', { name: 'Очередь' })).toBeTruthy();
  expect(apiClient.put).not.toHaveBeenCalled();
});

it.each([[undefined, 'Версия 3 · действующая'], [1, 'Версия 1 · на момент закрытия'], [2, 'Версия 2']])(
  'показывает выбранную версию %s и её собственные данные', async (number, title) => {
    read = selected => ({ ...card, actions: [], viewedVersion: selected ?? 3, atClosingVersion: 1,
      report: { ...card.report, note: `Содержимое ${selected ?? 3}`, currentVersion: 3, versions: [
        { number: 1, reason: 'Первая причина', author: { name: 'Первый менеджер' }, at: '2026-11-23T08:00:00Z' },
        { number: 2, reason: 'Вторая причина', author: { name: 'Второй менеджер' }, at: '2026-11-23T10:00:00Z' },
      ] } });
    mount(number ? `?number=${number}` : '');
    expect(await screen.findByText(`Содержимое ${number ?? 3}`)).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Версия отчёта' }).textContent).toContain(title);
    if (number === 2) expect(screen.getByText(/Вторая причина/)).toBeTruthy();
  },
);
