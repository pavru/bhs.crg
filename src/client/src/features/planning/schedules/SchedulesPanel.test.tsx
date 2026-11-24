// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { Schedule, ScheduleLink, ScheduleRebuildPreview, ScheduleWarning, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { LocaleContext } from '@/shared/hooks/useLocale';
import { NARROW } from '@/shared/hooks/useMedia';
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
    predecessor: { id: 'other', name: 'Подготовка основания', scheduleId: 'civil', scheduleName: 'Общестрой', sectionId: null },
    successor: { id: 'w2', name: 'Прокладка кабеля', scheduleId: 'graph', scheduleName: 'Электромонтаж', sectionId: null },
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
    if (url === '/constructions/site') return { data: { id: 'site', sections: [
      { id: 'section', name: 'Корпус 1' }, { id: 'section2', name: 'Корпус 2' },
    ] } };
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
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

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

function matchViewport(media: string) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === media, addEventListener: () => {}, removeEventListener: () => {},
  }));
}

function wideViewport() {
  matchViewport('(min-width: 1280px)');
}

function linkWith(predecessorId: string, successorId = 'w2'): ScheduleLink {
  return { id: 'l1', predecessorId, successorId, kind: 'fs', lag: 0, version: '5' };
}

async function openPredecessors() {
  fireEvent.click(await screen.findByRole('button', { name: 'Предшественники: Прокладка кабеля' }));
  return screen.findByRole('dialog', { name: 'Предшественники · Прокладка кабеля' });
}

async function openLinkActions(predecessor: string) {
  fireEvent.keyDown(await screen.findByRole('button', { name: `Действия связи с «${predecessor}»` }), { key: 'Enter' });
}

async function linkAction(name: string, predecessor = 'Монтаж лотков · По стройке в целом') {
  await openLinkActions(predecessor);
  fireEvent.click(await screen.findByRole('menuitem', { name }));
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
    expect(screen.queryByRole('tab', { name: 'Диаграмма' })).toBeNull();
    expect(callUrls()).not.toContain('/employees');
    expect(callUrls()).not.toContain('/work-types');
  });

  it('показывает custom-reader связанную работу соседнего графика без правки и запросов соседних графиков', async () => {
    live = { ...view, links: [{ ...linkWith('foundation'),
      predecessor: { id: 'foundation', name: 'Подготовка основания', scheduleId: 'civil', scheduleName: 'Общестрой', sectionId: 'section' },
    }] };
    mount();
    const modal = await openPredecessors();
    await within(modal).findByText('Подготовка основания · Корпус 1');
    expect(within(modal).getByText('Общестрой')).toBeTruthy();
    expect(within(modal).queryByRole('combobox')).toBeNull();
    expect(within(modal).queryByRole('button', { name: /Действия связи|Сохранить связь/ })).toBeNull();
    expect(callUrls()).not.toContain('/plan/schedules');
    expect(callUrls()).not.toContain('/plan/schedules/civil');
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

  it('берёт нерабочую дату с сервера, сохраняет отбор и возвращает фокус к строке списка', async () => {
    wideViewport();
    const previousGet = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation(async (url, config) =>
      url === '/plan/calendars/constructions/site/non-working-days' ? { data: {
        from: '2026-12-01', to: '2026-12-31', nonWorkingDays: ['2026-12-03'],
        gaps: [{ year: 2026, text: 'Календарь компании на 2026 год не заполнен.', code: 'calendar-missing' }],
      } } : previousGet(url, config));
    mount();
    await screen.findByRole('heading', { name: 'Электромонтаж' });
    click('Нарушения зависимостей · 1');
    fireEvent.click(screen.getByRole('tab', { name: 'Диаграмма' }));
    await screen.findByText('Для этих лет используются правила рабочей недели.');
    expect(document.querySelectorAll('[data-non-working-date]')).toHaveLength(1);
    expect(document.querySelector('[data-non-working-date="2026-12-03"]')).toBeTruthy();
    expect(screen.queryByText('Установка щитов')).toBeNull();
    expect(apiClient.get).toHaveBeenCalledWith('/plan/calendars/constructions/site/non-working-days', {
      params: { from: '2026-12-01', to: '2026-12-31' }, signal: expect.any(AbortSignal),
    });
    fireEvent.click(screen.getByRole('button', { name: /^Прокладка кабеля,.+Открыть в списке$/ }));
    const row = screen.getByRole('row', { name: /Прокладка кабеля/ });
    await waitFor(() => expect(row.contains(document.activeElement)).toBe(true));
    expect(screen.getByRole('tab', { name: 'Список' }).getAttribute('aria-selected')).toBe('true');
  });

  it('рисует связь между работами в разных периодах с обеих сторон', async () => {
    wideViewport();
    live = { ...view, links: [linkWith('w1')], works: [
      view.works[0], { ...view.works[1], start: '2027-01-10', end: '2027-01-12' },
    ] };
    const previousGet = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation(async (url, config) =>
      url === '/plan/calendars/constructions/site/non-working-days'
        ? { data: { ...config?.params, nonWorkingDays: [], gaps: [] } } : previousGet(url, config));
    mount();
    await screen.findByRole('heading', { name: 'Электромонтаж' });
    fireEvent.click(screen.getByRole('tab', { name: 'Диаграмма' }));
    await screen.findByText('Монтаж лотков → Прокладка кабеля (начало после периода)');
    const diagram = () => screen.getByLabelText('Сроки работ по рабочему календарю стройки');
    expect(diagram().querySelector('[marker-end]')).toBeTruthy();
    click('Следующий период');
    await screen.findByText('Монтаж лотков → Прокладка кабеля (окончание до периода)');
    expect(diagram().querySelector('[marker-end]')).toBeTruthy();
  });

  it('подписывает стрелку у границы по фактическим датам, когда обе полосы видны частично', async () => {
    wideViewport();
    live = { ...view, links: [linkWith('w1')], works: [
      { ...view.works[0], end: '2027-01-08' },
      { ...view.works[1], start: '2026-12-20', end: '2027-01-12' },
    ] };
    const previousGet = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation(async (url, config) =>
      url === '/plan/calendars/constructions/site/non-working-days'
        ? { data: { ...config?.params, nonWorkingDays: [], gaps: [] } } : previousGet(url, config));
    mount();
    await screen.findByRole('heading', { name: 'Электромонтаж' });
    fireEvent.click(screen.getByRole('tab', { name: 'Диаграмма' }));
    await screen.findByText('Монтаж лотков → Прокладка кабеля (окончание после периода)');
    const diagram = () => screen.getByLabelText('Сроки работ по рабочему календарю стройки');
    expect(within(diagram()).getAllByRole('button')).toHaveLength(2);
    expect(diagram().querySelector('[marker-end]')).toBeTruthy();
    click('Следующий период');
    await screen.findByText('Монтаж лотков → Прокладка кабеля (начало до периода)');
    expect(within(diagram()).getAllByRole('button')).toHaveLength(2);
    expect(diagram().querySelector('[marker-end]')).toBeTruthy();
  });

  it('оставляет предупреждение и переход к работе, когда у графика ещё нет дат', async () => {
    wideViewport();
    live = { ...view, works: [{ ...view.works[0], start: null, end: null }],
      warnings: [{ code: 'no-dates', text: 'У работы нет дат.', workId: 'w1', linkId: null, year: null }] };
    mount();
    await screen.findByRole('heading', { name: 'Электромонтаж' });
    fireEvent.click(screen.getByRole('tab', { name: 'Диаграмма' }));
    expect(screen.getByRole('img', { name: 'У работы нет дат.' })).toBeTruthy();
    expect(callUrls().some(url => url.endsWith('/non-working-days'))).toBe(false);
    click('Монтаж лотков');
    expect(screen.getByRole('row', { name: /Монтаж лотков/ })).toBeTruthy();
  });

  it('выбирает предшественника из соседнего графика и показывает все три работы цикла', async () => {
    permissions = [reader, editor];
    live = { ...view, works: [view.works[0],
      { ...view.works[1], sectionId: 'section' },
      { ...view.works[2], name: view.works[1].name, sectionId: 'section2' },
    ] };
    const civil = { ...view, id: 'civil', name: 'Общестрой', works: [makeWork('foundation', 'Подготовка основания')] };
    const previousGet = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation(async (url, config) => {
      if (url === '/plan/schedules') return { data: [live, civil] };
      if (url === '/plan/schedules/civil') return { data: civil };
      return previousGet(url, config);
    });
    vi.mocked(apiClient.post).mockRejectedValueOnce({ response: { status: 422, data: {
      code: 'schedule-cycle', error: 'Связь замыкает цикл работ.', cycle: [
        { id: 'w2', name: 'Прокладка кабеля', scheduleId: 'graph', scheduleName: 'Электромонтаж', sectionId: 'section' },
        { id: 'w3', name: 'Прокладка кабеля', scheduleId: 'graph', scheduleName: 'Электромонтаж', sectionId: 'section2' },
        { id: 'foundation', name: 'Подготовка основания', scheduleId: 'civil', scheduleName: 'Общестрой', sectionId: null },
      ],
    } } });
    mount();
    const row = await screen.findByRole('row', { name: /Прокладка кабеля.*Корпус 1/ });
    fireEvent.click(within(row).getByRole('button', { name: 'Предшественники: Прокладка кабеля' }));
    const modal = await screen.findByRole('dialog', { name: 'Предшественники · Прокладка кабеля' });
    await choose('График предшественника', 'Общестрой');
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'Предшественник' }) as HTMLButtonElement).disabled).toBe(false));
    await choose('Предшественник', 'Подготовка основания');
    fireEvent.change(screen.getByRole('textbox', { name: 'Задержка' }), { target: { value: '-2' } });
    click('Сохранить связь');
    await within(modal).findByText('Связь замыкает цикл работ.');
    expect(within(modal).getAllByRole('listitem').map(node => node.textContent)).toEqual([
      'Прокладка кабеля · Корпус 1 · Электромонтаж', 'Прокладка кабеля · Корпус 2 · Электромонтаж',
      'Подготовка основания · По стройке в целом · Общестрой',
    ]);
    expect(apiClient.post).toHaveBeenCalledWith(`${schedulePath}/links`, {
      predecessorId: 'foundation', successorId: 'w2', lag: -2, kind: 'fs',
    });
  });

  it.each(['lag', 'predecessor'])('сохраняет поле %s и берёт второе поле свежей связи после 409', async changed => {
    permissions = [reader, editor];
    const link = linkWith('w1');
    live = { ...view, links: [link] };
    const civil = { ...view, id: 'civil', name: 'Общестрой', works: [makeWork('foundation', 'Подготовка основания')] };
    const previousGet = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation(async (url, config) => {
      if (url === '/plan/schedules') return { data: [live, civil] };
      if (url === '/plan/schedules/civil') return { data: civil };
      return previousGet(url, config);
    });
    vi.mocked(apiClient.put).mockRejectedValueOnce({ response: { status: 409 } }).mockResolvedValueOnce({ data: {} });
    mount();
    await openPredecessors();
    await linkAction('Изменить');
    if (changed === 'lag') {
      fireEvent.change(screen.getByRole('textbox', { name: 'Задержка' }), { target: { value: '-2' } });
      live = { ...live, links: [{ ...link, version: '6', predecessorId: 'foundation',
        predecessor: { id: 'foundation', name: 'Подготовка основания', scheduleId: 'civil', scheduleName: 'Общестрой',
          sectionId: null } }] };
    } else {
      await choose('Предшественник', 'Установка щитов');
      live = { ...live, links: [{ ...link, version: '6', lag: 4 }] };
    }
    click('Сохранить связь');
    fireEvent.click(await screen.findByRole('button', { name: 'Перечитать график' }));
    await screen.findByText('График перечитан. Проверьте введённую связь и сохраните её.');
    await waitFor(() => expect((screen.getByRole('button', { name: 'Сохранить связь' }) as HTMLButtonElement).disabled).toBe(false));
    click('Сохранить связь');
    await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith(`${schedulePath}/links/l1`, {
      predecessorId: changed === 'lag' ? 'foundation' : 'w3', successorId: 'w2', kind: 'fs',
      lag: changed === 'lag' ? -2 : 4,
    }, { headers: { 'If-Match': '6' } }));
  });

  it('различает одноимённых предшественников и защищает ввод при смене связи', async () => {
    permissions = [reader, editor];
    live = { ...view, works: [
      { ...view.works[0], sectionId: 'section' }, view.works[1],
      { ...view.works[2], name: view.works[0].name, sectionId: 'section2' },
    ], links: [linkWith('w1'), { ...linkWith('w3'), id: 'l2' }] };
    let refuseSave: (error: unknown) => void = () => {};
    vi.mocked(apiClient.put).mockImplementationOnce(() => new Promise((_resolve, reject) => { refuseSave = reject; }));
    mount();
    const modal = await openPredecessors();
    const first = 'Монтаж лотков · Корпус 1';
    const second = 'Монтаж лотков · Корпус 2';
    await within(modal).findByRole('rowheader', { name: first });
    expect(within(modal).getByRole('rowheader', { name: second })).toBeTruthy();
    await linkAction('Изменить', first);
    const lag = screen.getByRole('textbox', { name: 'Задержка' }) as HTMLInputElement;
    fireEvent.change(lag, { target: { value: '7' } });

    async function expectLocked() {
      await openLinkActions(second);
      const edit = await screen.findByRole('menuitem', { name: /^Изменить/ });
      expect(edit.getAttribute('aria-disabled')).toBe('true');
      expect(screen.getByRole('menuitem', { name: /^Удалить/ }).getAttribute('aria-disabled')).toBe('true');
      fireEvent.click(edit);
      expect(lag.value).toBe('7');
      fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
    }

    await expectLocked();
    click('Сохранить связь');
    await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith(`${schedulePath}/links/l1`, expect.anything(), expect.anything()));
    await expectLocked();
    refuseSave({ response: { status: 409 } });
    await screen.findByRole('button', { name: 'Перечитать график' });
    expect(lag.value).toBe('7');
    click('Отменить правку');
    await linkAction('Удалить', second);
    expect(screen.getByRole('dialog', { name: `Удалить связь с «${second}»?` })).toBeTruthy();
  });

  it('показывает сохранение связи в шапке узкого окна', async () => {
    permissions = [reader, editor];
    matchViewport(NARROW);
    mount();
    const dialog = within(await openPredecessors());
    const close = dialog.getByRole('button', { name: 'Закрыть' });
    const save = await dialog.findByRole('button', { name: 'Сохранить связь' });
    expect(close.parentElement?.contains(save)).toBe(true);
    expect(close.parentElement?.contains(dialog.getByRole('textbox', { name: 'Задержка' }))).toBe(false);
  });

  it('перечитывает связь после отказа удаления, повтор подтверждает свежую версию', async () => {
    permissions = [reader, editor];
    const link = linkWith('w1');
    live = { ...view, links: [link] };
    vi.mocked(apiClient.delete).mockImplementationOnce(async () => {
      live = { ...live, links: [{ ...link, version: '6' }] };
      throw { response: { status: 409, data: { error: 'Связь тем временем изменили.' } } };
    }).mockResolvedValueOnce({ data: undefined });
    mount();
    await openPredecessors();
    await linkAction('Удалить');
    click('Удалить связь');
    await screen.findByText('Связь тем временем изменили.');
    expect(apiClient.delete).toHaveBeenLastCalledWith(`${schedulePath}/links/l1`, { headers: { 'If-Match': '5' } });
    click('Понятно');
    await linkAction('Удалить');
    click('Удалить связь');
    await waitFor(() => expect(apiClient.delete).toHaveBeenLastCalledWith(
      `${schedulePath}/links/l1`, { headers: { 'If-Match': '6' } },
    ));
  });
});
