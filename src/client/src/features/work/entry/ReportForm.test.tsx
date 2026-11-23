// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { reportQuery, type EntrySites, type ReportInput, type ReportMode, type WorkReport } from '@/shared/api/workReports';
import { formatLocale, setFormatLocale } from '@/shared/format/format';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { SnackbarCtx } from '@/shared/ui/Snackbar';
import { ReportForm } from './ReportForm';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
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
const units = [{ id: 'u1', name: 'м', archived: false }, { id: 'u2', name: 'шт.', archived: false }];
const types = [
  { id: 'w1', displayName: 'Прокладка кабеля', unitId: 'u1', unitName: 'м', archived: false,
    requiresLocation: true, requiresMaterials: true, variants: [{ id: 'v1', name: 'В лотке', archived: false }] },
  { id: 'w2', displayName: 'Монтаж коробки', unitId: 'u2', unitName: 'шт.', archived: false,
    requiresLocation: false, requiresMaterials: false, variants: [] },
];
const toast = { success: vi.fn(), info: vi.fn(), error: vi.fn(), apiError: vi.fn() };
let current: WorkReport;
let materialReason: string | null;

beforeEach(() => {
  vi.resetAllMocks();
  current = report;
  materialReason = null;
  vi.mocked(apiClient.get).mockImplementation(async path => {
    if (path === '/work/photos/limits') return { data: { count: 5, megabytes: 15, maxBytes: 15 * 1024 * 1024,
      countText: 'Не больше 5 фото на строку.', sizeText: 'Фото больше 15 МБ.' } };
    if (path.endsWith('/preview')) return new Promise(() => {});
    if (path.endsWith('/catalog/work-types')) return { data: { items: types } };
    if (path.endsWith('/catalog/units')) return { data: { items: units } };
    if (path.endsWith('/catalog/sections')) return { data: { items: [{ id: 's1', name: 'Корпус А' }] } };
    if (path.endsWith('/catalog/materials')) return { data: { reason: materialReason,
      items: materialReason ? [] : [{ id: 'm1', displayName: 'Кабель ВВГ', archived: false }] } };
    if (path.endsWith('/crew-candidates')) return { data: { items: [{ id: 'e2', fullName: 'Сергеев' }] } };
    return { data: path === '/work/sites/mine' || path.endsWith('/sites') ? sites : current };
  });
});
afterEach(cleanup);

function mount(id: string, mode: ReportMode = 'own', client = new QueryClient()) {
  const path = mode === 'own' ? '/my-reports' : '/work-review/entry';
  render(<SnackbarCtx.Provider value={toast}><QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[`${path}/${id}`]}>
      <NavigationGuardProvider><Routes>
        <Route path={path} element={<h2>Список отчётов</h2>} />
        <Route path={`${path}/:reportId`} element={<ReportForm mode={mode} />} />
      </Routes></NavigationGuardProvider>
    </MemoryRouter>
  </QueryClientProvider></SnackbarCtx.Provider>);
}

const button = (name: string) => screen.getByRole('button', { name });

async function pickPhotoForNewReport(file: File) {
  mount('new');
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить строку' }));
  fireEvent.click(button('Вид работы, строка 1'));
  fireEvent.click(await screen.findByRole('option', { name: 'Монтаж коробки' }));
  await screen.findByText('До 5 фото на строку · до 15 МБ на файл');
  fireEvent.change(screen.getByLabelText('Файлы фото строки'), { target: { files: [file] } });
}

function currentFromCreate(input: unknown) {
  const form = input as ReportInput & { id: string };
  const lines = form.lines.map(line => ({ ...line,
    photos: (line.photos ?? []).map(photo => ({ ...photo, state: 'pending' as const, canDeliver: true })),
  }));
  current = { ...report, ...form, employeeId: report.employeeId, lines };
  return current;
}

it('объявляет фото до отправки, показывает 413 у выбранного файла и повторяет с тем же id', async () => {
  const file = new File(['фото'], 'смена.jpg', { type: 'image/jpeg' });
  vi.mocked(apiClient.put).mockImplementationOnce(async (_, input) => {
    const selected = (input as { lines: { photos: { id: string }[] }[] }).lines[0].photos;
    expect(selected).toEqual([{ id: 'p1' }, { id: expect.stringMatching(/^[0-9a-f-]{36}$/), originalName: file.name }]);
    return { data: { ...report, version: '2', lines: [{ ...report.lines[0], photos: selected.map(photo => ({
      ...photo, state: 'pending', canDeliver: true,
    })) }] } };
  }).mockRejectedValueOnce(refusal(413, { error: 'Фото больше 2 МБ — предел задан в настройках модуля.' }))
    .mockResolvedValueOnce({ data: { state: 'stored' } });
  mount('r1');
  await screen.findByText('До 5 фото на строку · до 15 МБ на файл');
  fireEvent.change(screen.getByLabelText('Файлы фото строки'), { target: { files: [file] } });
  expect(screen.getByText('Загрузится после сохранения')).toBeTruthy();
  expect(apiClient.put).not.toHaveBeenCalled();
  fireEvent.click(button('Сохранить'));
  await screen.findByText('Фото больше 2 МБ — предел задан в настройках модуля.');
  expect(within(screen.getByText(file.name).closest('li')!).getByRole('alert')).toBeTruthy();
  const first = vi.mocked(apiClient.put).mock.calls[1];
  expect(first[0]).toMatch(/^\/work\/reports\/r1\/lines\/l1\/photos\/[0-9a-f-]{36}$/);
  expect(first[1]).toBe(file);
  expect(first[2]?.headers).toEqual({ 'Content-Type': 'application/octet-stream' });
  fireEvent.click(button('Повторить'));
  await screen.findByRole('button', { name: 'Открыть фото: смена.jpg' });
  expect(vi.mocked(apiClient.put).mock.calls[2].slice(0, 2)).toEqual(first.slice(0, 2));
});

it('начинает загрузку фото нового черновика после перехода на созданный отчёт', async () => {
  const file = new File(['фото'], 'новый.jpg', { type: 'image/jpeg' });
  vi.mocked(apiClient.post).mockImplementationOnce(async (_, input) => ({ data: currentFromCreate(input) }));
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { state: 'stored' } });
  await pickPhotoForNewReport(file);
  fireEvent.click(button('Создать черновик'));
  await screen.findByRole('button', { name: 'Открыть фото: новый.jpg' });
  const line = current.lines[0];
  expect(apiClient.put).toHaveBeenCalledExactlyOnceWith(`/work/reports/${current.id}/lines/${line.id}/photos/${line.photos[0].id}`,
    file, expect.objectContaining({ headers: { 'Content-Type': 'application/octet-stream' } }));
  expect(screen.queryByRole('heading', { name: 'Новый отчёт' })).toBeNull();
});

it('после потерянного ответа не восстанавливает снятое фото и записывает пустой состав с версией', async () => {
  const file = new File(['фото'], 'снятое.jpg', { type: 'image/jpeg' });
  vi.mocked(apiClient.post).mockImplementationOnce(async (_, input) => {
    currentFromCreate(input);
    throw new Error('Ответ потерян');
  }).mockImplementationOnce(async () => ({ data: current }));
  await pickPhotoForNewReport(file);
  fireEvent.click(button('Создать черновик'));
  await screen.findByText('Ответ потерян');
  const first = vi.mocked(apiClient.post).mock.lastCall?.[1];
  fireEvent.click(button(`Удалить фото: ${file.name}`));
  fireEvent.click(button('Создать черновик'));
  await screen.findByText('Черновик уже создан. Новые значения ещё не сохранены.');
  expect(apiClient.post).toHaveBeenLastCalledWith('/work/reports', first);
  expect(screen.getByText('Фото пока нет')).toBeTruthy();
  vi.mocked(apiClient.put).mockImplementationOnce(async () => ({ data: {
    ...current, version: '2', lines: [{ ...current.lines[0], photos: [] }],
  } }));
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledExactlyOnceWith(`/work/reports/${current.id}`,
    expect.objectContaining({ lines: [expect.objectContaining({ photos: [] })] }), { headers: { 'If-Match': '1' } }));
});

it('при уходе сохраняет часы и объявление фото, пока доставка ещё не завершилась', async () => {
  const file = new File(['фото'], 'крепёж.jpg', { type: 'image/jpeg' });
  vi.mocked(apiClient.put).mockImplementationOnce(async (_, input) => ({ data: {
    ...report, version: '2', hours: 7, lines: [{ ...report.lines[0], photos:
      ((input as ReportInput).lines[0].photos ?? []).map(photo => ({ ...photo, state: 'pending', canDeliver: true })),
    }],
  } })).mockImplementation(() => new Promise(() => {}));
  mount('r1');
  await screen.findByText('До 5 фото на строку · до 15 МБ на файл');
  fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7' } });
  fireEvent.change(screen.getByLabelText('Файлы фото строки'), { target: { files: [file] } });
  fireEvent.click(button('К отчётам'));
  expect(await screen.findByText(/При уходе без сохранения правки и новые объявления фото будут потеряны/)).toBeTruthy();
  fireEvent.click(button('Сохранить и перейти'));
  await screen.findByRole('heading', { name: 'Список отчётов' });
  expect(apiClient.put).toHaveBeenNthCalledWith(1, '/work/reports/r1', expect.objectContaining({
    hours: 7, lines: [expect.objectContaining({ photos: [{ id: 'p1' }, expect.objectContaining({ originalName: file.name })] })],
  }), { headers: { 'If-Match': '1' } });
});

it('сверяет версию при удалении фото и сохраняет правку часов после отказа 409 и перечитывания', async () => {
  vi.mocked(apiClient.delete).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  const hours = await screen.findByRole<HTMLInputElement>('textbox', { name: 'Часы за смену' });
  fireEvent.change(hours, { target: { value: '7' } });
  fireEvent.click(button('Удалить фото: Фото 1'));
  await screen.findByText(/Отчёт тем временем изменили — перечитайте/);
  expect(apiClient.delete).toHaveBeenLastCalledWith('/work/reports/r1/lines/l1/photos/p1', {
    params: { changeId: expect.any(String) }, headers: { 'If-Match': '1' },
  });
  expect(button('Удалить фото: Фото 1')).toBeTruthy();
  current = { ...report, version: '2' };
  fireEvent.click(button('Перечитать'));
  await waitFor(() => expect(screen.queryByText(/Отчёт тем временем изменили/)).toBeNull());
  vi.mocked(apiClient.delete).mockResolvedValueOnce({ data: { ...current, version: '3', lines: [{ ...current.lines[0], photos: [] }] } });
  fireEvent.click(button('Удалить фото: Фото 1'));
  await screen.findByText('Фото пока нет');
  expect(hours.value).toBe('7');
  expect(apiClient.delete).toHaveBeenLastCalledWith('/work/reports/r1/lines/l1/photos/p1', {
    params: { changeId: expect.any(String) }, headers: { 'If-Match': '2' },
  });
});

it('после закрытия разрешает выбрать файл только для фото, у которого сервер разрешил досылку', async () => {
  current = { ...report, periodClosed: true, status: 'accepted', lines: [{ ...report.lines[0], photos: [
    { id: 'before', state: 'pending', canDeliver: true, originalName: 'досылка.jpg' },
    { id: 'after', state: 'pending', canDeliver: false, originalName: 'исправление.jpg' },
  ] }] };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { state: 'stored' } });
  mount('r1');
  await screen.findByText('Период закрыт.');
  await screen.findByText('До 5 фото на строку · до 15 МБ на файл');
  expect(screen.queryByRole('button', { name: 'Добавить фото' })).toBeNull();
  expect(screen.queryByRole('button', { name: /Удалить фото/ })).toBeNull();
  expect(screen.getAllByRole('button', { name: 'Дослать' })).toHaveLength(1);
  fireEvent.click(within(screen.getByText('досылка.jpg').closest('li')!).getByRole('button', { name: 'Дослать' }));
  const file = new File(['фото'], 'досылка.jpg', { type: 'image/jpeg' });
  fireEvent.change(screen.getByLabelText('Файлы фото строки'), { target: { files: [file] } });
  await screen.findByRole('button', { name: 'Открыть фото: досылка.jpg' });
  expect(apiClient.put).toHaveBeenCalledExactlyOnceWith('/work/reports/r1/lines/l1/photos/before', file,
    expect.objectContaining({ headers: { 'Content-Type': 'application/octet-stream' } }));
});

it('сохраняет неизменённый дробный объём при правке часов в en-US', async () => {
  const locale = formatLocale();
  setFormatLocale('en-US');
  try {
    current = { ...report, lines: [{ ...report.lines[0], volume: 1.234 }] };
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: current });
    mount('r1');
    const volume = await screen.findByRole<HTMLInputElement>('textbox', { name: 'Объём, строка 1' });
    expect(volume.value).toBe('1,234');
    fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7' } });
    fireEvent.click(button('Сохранить'));
    await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/work/reports/r1', expect.objectContaining({
      hours: 7, lines: [expect.objectContaining({ volume: 1.234 })],
    }), { headers: { 'If-Match': '1' } }));
  } finally { setFormatLocale(locale); }
});

it('при отказе 409 сохраняет выбранную смену и после перечитывания отправляет свежую версию и строки', async () => {
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  fireEvent.click(await screen.findByRole('tab', { name: 'Ночь' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Место работ' }), { target: { value: 'Этаж 2' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  expect((await screen.findByRole('alert')).textContent).toContain('Отчёт тем временем изменили');
  expect(screen.getByRole('tab', { name: 'Ночь' }).getAttribute('aria-selected')).toBe('true');
  current = { ...report, version: '2', lines: [{ ...report.lines[0], volume: 14 }] };
  fireEvent.click(screen.getByRole('button', { name: 'Перечитать' }));
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { ...current, shift: 'night', version: '3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/work/reports/r1',
    expect.objectContaining({ shift: 'night', changeId: expect.any(String),
      lines: [expect.objectContaining({ volume: 14, location: 'Этаж 2' })] }),
    { headers: { 'If-Match': '2' } }));
  const body = vi.mocked(apiClient.put).mock.lastCall?.[1] as { lines: unknown[] };
  expect(body.lines[0]).not.toHaveProperty('photos');
});

it('у вида без вариантов скрывает поле, сбрасывает прежний вариант и предлагает единицу нового вида', async () => {
  current = { ...report, lines: [{ ...report.lines[0], variantId: 'v1' }] };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: current });
  mount('r1');
  expect(await screen.findByRole('combobox', { name: 'Вариант' })).toBeTruthy();
  fireEvent.click(button('Вид работы, строка 1'));
  const dialog = within(await screen.findByRole('dialog'));
  const option = await dialog.findByRole('option', { name: 'Монтаж коробки' });
  const search = dialog.getByRole('combobox', { name: 'Поиск по справочнику' });
  expect(document.activeElement).toBe(search);
  fireEvent.keyDown(search, { key: 'ArrowDown' });
  fireEvent.keyDown(search, { key: 'ArrowDown' });
  expect(search.getAttribute('aria-activedescendant')).toBe(option.id);
  expect(option.getAttribute('aria-selected')).toBe('false');
  fireEvent.keyDown(search, { key: 'Enter' });
  await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Вариант' })).toBeNull());
  expect(button('Единица, строка 1').textContent).toContain('шт.');
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/work/reports/r1', expect.objectContaining({
    lines: [expect.objectContaining({ workTypeId: 'w2', variantId: null, unitId: 'u2' })],
  }), { headers: { 'If-Match': '1' } }));
});

it('добавляет и убирает черновую строку, сохраняет материалы, часы и выбранного участника с датой стройки', async () => {
  mount('r1');
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить строку' }));
  expect(button('Вид работы, строка 2')).toBeTruthy();
  fireEvent.click(button('Удалить строку 2'));
  expect(screen.queryByRole('button', { name: 'Вид работы, строка 2' })).toBeNull();
  expect(toast.success).toHaveBeenCalledWith('Строка 2 удалена.', expect.objectContaining({
    action: expect.objectContaining({ label: 'Вернуть' }),
  }));
  fireEvent.click(button('Подробности строки 1'));
  fireEvent.click(button('Добавить материал'));
  fireEvent.click(await within(await screen.findByRole('dialog')).findByRole('option', { name: 'Кабель ВВГ' }));
  fireEvent.click(screen.getByRole('checkbox', { name: 'Бригадная работа' }));
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Сергеев' }));
  expect(apiClient.get).toHaveBeenCalledWith('/work/constructions/site-a/crew-candidates',
    expect.objectContaining({ params: { date: report.date } }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7.5' } });
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: current });
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/work/reports/r1', expect.objectContaining({
    hours: 7.5, lines: [expect.objectContaining({ isCrew: true, participants: ['e2'],
      materials: [{ nomenclatureId: 'm1', note: null }] })],
  }), { headers: { 'If-Match': '1' } }));
});

it('после автоподачи восстанавливает ключ и позволяет сохранить набранный объём', async () => {
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  fireEvent.click(await screen.findByRole('tab', { name: 'Ночь' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Объём, строка 1' }), { target: { value: '12' } });
  fireEvent.click(button('Сохранить'));
  current = { ...report, status: 'submitted', version: '2' };
  fireEvent.click(await screen.findByRole('button', { name: 'Перечитать' }));
  expect(await screen.findByText(/Стройка, дата и смена восстановлены/)).toBeTruthy();
  expect(screen.getByRole('tab', { name: 'День' }).getAttribute('aria-selected')).toBe('true');
  expect((screen.getByRole('tab', { name: 'Ночь' }) as HTMLButtonElement).disabled).toBe(true);
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: current });
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/work/reports/r1', expect.objectContaining({
    shift: 'day', lines: [expect.objectContaining({ volume: 12 })],
  }), { headers: { 'If-Match': '2' } }));
});

it('при вводе за сотрудника подбирает бригаду через адрес менеджера', async () => {
  mount('r1', 'entry');
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Бригадная работа' }));
  expect(await screen.findByRole('checkbox', { name: 'Сергеев' })).toBeTruthy();
  expect(apiClient.get).toHaveBeenCalledWith('/work/entry/sites/site-a/crew-candidates',
    expect.objectContaining({ params: { date: report.date } }));
  expect(apiClient.get).not.toHaveBeenCalledWith('/work/constructions/site-a/crew-candidates', expect.anything());
});

it('показывает причину пустого ответа справочника материалов', async () => {
  materialReason = 'Справочник номенклатуры не заведён.';
  mount('r1');
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить материал' }));
  expect(await screen.findByText(materialReason)).toBeTruthy();
});

it('при 422 раскрывает вторую строку и переводит фокус к полю материалов', async () => {
  current = { ...report, lines: [report.lines[0], { ...report.lines[0], id: 'l2', photos: [] }] };
  vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(422, {
    error: 'Отчёт не подан — не заполнено: строка 2, материалы.',
    requiredMissing: [{ line: 2, lineId: 'l2', field: 'materials' }],
  }));
  mount('r1');
  expect(await screen.findByText('Нужно для ИД · место работ, материалы')).toBeTruthy();
  fireEvent.click(button('Подать отчёт'));
  expect(await screen.findByText('Отчёт не подан — не заполнено: строка 2, материалы.')).toBeTruthy();
  expect(button('Подробности строки 2').getAttribute('aria-expanded')).toBe('true');
  expect(await screen.findByText('Добавьте материал для подачи отчёта.')).toBeTruthy();
  await waitFor(() => expect(document.activeElement).toBe(button('Добавить материал')));
});

it('после 409 просит заново подтвердить удаление строки, к которой успели добавить фото', async () => {
  current = { ...report, lines: [{ ...report.lines[0], photos: [] }] };
  const withPhoto = { ...report, version: '2' };
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  fireEvent.click(await screen.findByRole('button', { name: 'Удалить строку 1' }));
  fireEvent.click(button('Сохранить'));
  current = withPhoto;
  fireEvent.click(await screen.findByRole('button', { name: 'Перечитать' }));
  expect(await screen.findByText(/подтвердите удаление заново/)).toBeTruthy();
  fireEvent.click(button('Удалить строку 1'));
  expect(await screen.findByText(/фото \(1\)/)).toBeTruthy();
});

it('в поданном отчёте оставляет шапку запертой и сохраняет правку строки с версией', async () => {
  current = { ...report, status: 'submitted' };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: current });
  mount('r1');
  expect(await screen.findByText('Изменения строк будут видны менеджеру как правка после подачи.')).toBeTruthy();
  expect((screen.getByRole('tab', { name: 'Ночь' }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.queryByRole('button', { name: 'Подать отчёт' })).toBeNull();
  fireEvent.change(screen.getByRole('textbox', { name: 'Объём, строка 1' }), { target: { value: '14' } });
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith('/work/reports/r1', expect.objectContaining({
    lines: [expect.objectContaining({ volume: 14 })],
  }), { headers: { 'If-Match': '1' } }));
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
  fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7' } });
  await waitFor(() => expect((screen.getByRole('button', { name: 'Создать черновик' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Создать черновик' }));
  expect(await screen.findByText('Черновик уже создан. Новые значения ещё не сохранены.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenLastCalledWith('/work/reports', attempt);
  expect(screen.getByRole('tab', { name: 'День' }).getAttribute('aria-selected')).toBe('true');
  expect((screen.getByRole('button', { name: 'Подать отчёт' }) as HTMLButtonElement).disabled).toBe(true);
  vi.mocked(apiClient.put).mockImplementationOnce(async () => {
    current = { ...current, date, shift: 'day', hours: 7, version: '2' };
    return { data: current };
  });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledWith(`/work/reports/${attempt.id}`,
    expect.objectContaining({ date, shift: 'day', hours: 7 }), { headers: { 'If-Match': '1' } }));
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
