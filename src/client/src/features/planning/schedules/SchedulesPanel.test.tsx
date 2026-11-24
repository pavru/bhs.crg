// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { Schedule, ScheduleRebuildPreview, ScheduleWarning, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { LocaleContext } from '@/shared/hooks/useLocale';
import { SchedulesPanel } from './SchedulesPanel';

let permissions: string[];
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useCan: () => ({ permission: (code: string) => permissions.includes(code) }),
}));
vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

const reader = 'plan.schedule.read';
const editor = 'plan.schedule.edit';
const schedulePath = '/plan/schedules/graph';
const root = '/document-sets/site/schedule';
const employee = { id: 'emp', name: 'Иван Орлов', archived: false };
const makeWork = (id: string, name: string): ScheduleWork => ({
  id, name, sectionId: null, manual: true, workTypeId: 'type', sourceRowId: null, planItemId: 'item',
  durationWorkingDays: 19, unitId: 'unit', unitName: 'м', start: '2026-12-01', end: '2026-12-03',
  volume: 100, employeeIds: [employee.id], assignees: [employee], version: '5',
});
const warnings: ScheduleWarning[] = ['w1', 'w2'].map(workId => ({
  code: 'starts-before-predecessor', text: 'Работа начинается раньше предшественника.', workId, linkId: 'l1', year: null,
}));
const view: Schedule = {
  id: 'graph', constructionId: 'site', revisionId: 'rev', version: '3', name: 'Электромонтаж',
  skipped: { unmapped: 2 }, marks: [{ code: 'distribution-changed', text: 'Распределение изменено после построения.' }],
  warnings, links: [],
  works: [makeWork('w1', 'Монтаж лотков'), makeWork('w2', 'Прокладка кабеля'), makeWork('w3', 'Установка щитов')],
};
const preview: ScheduleRebuildPreview = {
  version: '3', fingerprint: 'seen', added: [], changed: [], removed: [], skipped: { unmapped: 4 },
  removedLinks: [{
    id: 'l1', kind: 'fs', lag: -2,
    predecessor: { id: 'other', name: 'Подготовка основания', scheduleId: 'civil', scheduleName: 'Общестрой' },
    successor: { id: 'w2', name: 'Прокладка кабеля', scheduleId: 'graph', scheduleName: 'Электромонтаж' },
  }],
};
let live: Schedule;
let showsPreview: ScheduleRebuildPreview;

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});

beforeEach(() => {
  vi.resetAllMocks();
  permissions = [reader];
  live = view;
  showsPreview = preview;
  vi.mocked(apiClient.get).mockImplementation(async (url: string) => {
    if (url === '/constructions/site') return { data: { id: 'site', sections: [{ id: 'section', name: 'Корпус 1' }] } };
    if (url === '/plan/schedules') return { data: [live] };
    if (url === schedulePath) return { data: live };
    if (url === `${schedulePath}/rebuild-preview`) return { data: showsPreview };
    if (url === '/employees') return { data: [
      { id: 'emp', displayName: 'Иван Орлов', archived: false, account: { state: 'linked' } },
      { id: 'old', displayName: 'Сергей Белов', archived: true, account: { state: 'none' } },
    ] };
    if (url === '/plan/estimates') return { data: { items: [{
      id: 'estimate', name: 'Электроснабжение', currentRevisionId: 'rev',
      revisions: [{ id: 'rev', number: 3, current: true }],
    }] } };
    throw new Error(`Unexpected request: ${url}`);
  });
});
afterEach(cleanup);

function mount(path = `${root}/graph`, locale = 'ru-RU') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}>
    <LocaleContext.Provider value={[locale, () => {}, 'idle']}>
      <MemoryRouter initialEntries={[path]}><Routes>
        <Route path="/document-sets/:constructionId/:panel/*" element={<SchedulesPanel constructionId="site" />} />
      </Routes></MemoryRouter>
    </LocaleContext.Provider>
  </QueryClientProvider>);
}

const callUrls = () => vi.mocked(apiClient.get).mock.calls.map(([url]) => url);
const click = (name: string) => fireEvent.click(screen.getByRole('button', { name }));
async function choose(field: string, option: string) {
  fireEvent.keyDown(screen.getByRole('combobox', { name: field }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

describe('график стройки', () => {
  it('показывает custom-reader подписи, длительность и обе строки нарушения без запросов к справочникам', async () => {
    mount();
    await screen.findByRole('heading', { name: 'Электромонтаж' });
    expect(screen.getAllByText('19')).toHaveLength(3);
    expect(screen.getAllByText('По стройке в целом')).toHaveLength(3);
    expect(screen.getAllByText('Иван Орлов')).toHaveLength(3);
    expect(screen.getAllByRole('img', { name: 'Работа начинается раньше предшественника.' })).toHaveLength(2);
    click('Нарушения зависимостей · 1');
    expect(screen.queryByText('Установка щитов')).toBeNull();
    expect(screen.getByText('Монтаж лотков')).toBeTruthy();
    expect(screen.getByText('Прокладка кабеля')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Добавить работу' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Предпросмотр перестроения' })).toBeNull();
    expect(screen.queryByRole('textbox', { name: 'Объём' })).toBeNull();
    expect(callUrls()).not.toContain('/employees');
    expect(callUrls()).not.toContain('/work-types');
  });

  it('сохраняет черновик custom-editor после 409 и берёт новую версию с неизменёнными полями', async () => {
    permissions = [reader, editor];
    vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status: 409 } }).mockResolvedValueOnce({ data: {} });
    mount();
    fireEvent.click(await screen.findByRole('button', { name: 'Монтаж лотков' }));
    expect(screen.queryByRole('button', { name: 'Добавить работу' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Выбрать исполнителей' })).toBeNull();
    await screen.findByText('Для выбора исполнителей нужно право чтения справочника сотрудников.');
    fireEvent.change(screen.getByRole('textbox', { name: 'Объём' }), { target: { value: '175' } });
    click('Сохранить работу');
    await screen.findByText('Работу тем временем изменили. Перечитайте график. Ваш ввод сохранён.');
    expect((screen.getByRole('textbox', { name: 'Объём' }) as HTMLInputElement).value).toBe('175');
    expect(vi.mocked(apiClient.put).mock.calls[0]?.[2]).toEqual({ headers: { 'If-Match': '5' } });
    live = { ...view, works: [{ ...view.works[0], version: '6', end: '2026-12-04' }, ...view.works.slice(1)] };
    click('Перечитать');
    await screen.findByText('График перечитан. Проверьте значения и сохраните работу.');
    expect((screen.getByRole('textbox', { name: 'Объём' }) as HTMLInputElement).value).toBe('175');
    click('Сохранить работу');
    await waitFor(() => expect(vi.mocked(apiClient.put)).toHaveBeenLastCalledWith(`${schedulePath}/works/w1`, {
      volume: 175, start: '2026-12-01', end: '2026-12-04', employeeIds: ['emp'],
    }, { headers: { 'If-Match': '6' } }));
    expect(callUrls()).not.toContain('/employees');
    expect(callUrls()).not.toContain('/work-types');
  });

  it.each([
    { locale: 'en-US', initial: '1.234', typed: '2.345' },
    { locale: 'ru-RU', initial: '1,234', typed: '2,345' },
    { locale: 'de-DE', initial: '1,234', typed: '2,345' },
  ])('сохраняет точный объём при смене исполнителя и разбирает ввод на $locale', async ({ locale, initial, typed }) => {
    permissions = [reader, editor, 'core.employees.read'];
    live = { ...view, works: [{ ...view.works[0], volume: 1.234 }, ...view.works.slice(1)] };
    vi.mocked(apiClient.put).mockResolvedValue({ data: {} });
    mount(`${root}/graph`, locale);
    fireEvent.click(await screen.findByRole('button', { name: 'Монтаж лотков' }));
    expect((screen.getByRole('textbox', { name: 'Объём' }) as HTMLInputElement).value).toBe(initial);
    click('Выбрать исполнителей');
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Отметить: Иван Орлов' }));
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Исполнители' })).getByRole('button', { name: 'Выбрать' }));
    click('Сохранить работу');
    await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith(`${schedulePath}/works/w1`, {
      volume: 1.234, start: '2026-12-01', end: '2026-12-03', employeeIds: [],
    }, { headers: { 'If-Match': '5' } }));
    await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Объём' })).toBeNull());
    click('Монтаж лотков');
    fireEvent.change(screen.getByRole('textbox', { name: 'Объём' }), { target: { value: typed } });
    click('Сохранить работу');
    await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith(`${schedulePath}/works/w1`, {
      volume: 2.345, start: '2026-12-01', end: '2026-12-03', employeeIds: ['emp'],
    }, { headers: { 'If-Match': '5' } }));
  });

  it('после отказа удаления перечитывает график и подтверждает удаление уже с новой версией', async () => {
    permissions = [reader, editor];
    vi.mocked(apiClient.delete).mockImplementationOnce(async () => {
      live = { ...view, works: [{ ...view.works[0], version: '6' }, ...view.works.slice(1)] };
      throw { response: { status: 409, data: { error: 'Работу тем временем изменили.' } } };
    }).mockResolvedValueOnce({ data: undefined });
    mount();
    async function confirmDelete() {
      const menu = await screen.findByRole('button', { name: 'Действия работы «Монтаж лотков»' });
      fireEvent.keyDown(menu, { key: 'Enter' });
      fireEvent.click(await screen.findByRole('menuitem', { name: 'Удалить' }));
      await screen.findByText('Связи этой работы с другими работами тоже будут удалены.');
      click('Удалить работу');
    }
    await confirmDelete();
    await screen.findByText('Работу тем временем изменили.');
    expect(apiClient.delete).toHaveBeenLastCalledWith(`${schedulePath}/works/w1`, { headers: { 'If-Match': '5' } });
    click('Понятно');
    await confirmDelete();
    await waitFor(() => expect(apiClient.delete).toHaveBeenLastCalledWith(
      `${schedulePath}/works/w1`, { headers: { 'If-Match': '6' } },
    ));
  });

  it('оставляет ручной график custom-editor без чтения смет', async () => {
    permissions = [reader, editor];
    mount(root);
    await screen.findByRole('link', { name: 'Электромонтаж' });
    expect(screen.queryByRole('button', { name: 'Построить из сметы' })).toBeNull();
    click('Новый график');
    await screen.findByRole('dialog', { name: 'Новый график' });
    expect(callUrls()).not.toContain('/plan/estimates');
  });

  it('строит график из выбранной редакции для менеджера и показывает число пропущенных строк', async () => {
    permissions = [reader, editor, 'plan.estimate.read', 'core.catalog.read', 'core.employees.read'];
    vi.mocked(apiClient.post).mockResolvedValue({ data: view });
    mount(root);
    click('Построить из сметы');
    await screen.findByRole('combobox', { name: 'Смета' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Название графика' }), { target: { value: 'Электромонтаж' } });
    await choose('Смета', 'Электроснабжение');
    click('Построить');
    await screen.findByText('Не вошли 2 несопоставленные строки сметы.');
    expect(apiClient.post).toHaveBeenCalledWith('/plan/schedules', {
      constructionId: 'site', revisionId: 'rev', creationKey: expect.any(String), name: 'Электромонтаж',
    });
    click('Монтаж лотков');
    click('Выбрать исполнителей');
    expect((await screen.findByRole('checkbox', { name: 'Отметить: Сергей Белов' }) as HTMLInputElement).disabled).toBe(true);
  });

  it('показывает новый предпросмотр и применяет его только после повторного подтверждения', async () => {
    permissions = [reader, editor];
    const next = { ...preview, version: '4', fingerprint: 'fresh', skipped: { unmapped: 6 } };
    vi.mocked(apiClient.post).mockRejectedValueOnce({
      response: { status: 409, data: { error: 'Данные изменились после предпросмотра.', preview: next } },
    }).mockResolvedValueOnce({ data: { ...view, marks: [] } });
    mount();
    fireEvent.click(await screen.findByRole('button', { name: 'Предпросмотр перестроения' }));
    const modal = await screen.findByRole('dialog', { name: 'Перестроить по распределению' });
    await within(modal).findByText('Общестрой · Подготовка основания');
    expect(within(modal).getByText('Электромонтаж · Прокладка кабеля')).toBeTruthy();
    click('Перестроить');
    await within(modal).findByText('Не вошло 6 несопоставленных строк сметы.');
    expect(apiClient.post).toHaveBeenCalledTimes(1);
    expect(apiClient.post).toHaveBeenLastCalledWith(`${schedulePath}/rebuild`, { fingerprint: 'seen' }, { headers: { 'If-Match': '3' } });
    click('Перестроить');
    await waitFor(() => expect(apiClient.post).toHaveBeenLastCalledWith(`${schedulePath}/rebuild`,
      { fingerprint: 'fresh' }, { headers: { 'If-Match': '4' } }));
  });
});
