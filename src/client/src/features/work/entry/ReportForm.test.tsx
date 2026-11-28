// @vitest-environment jsdom
import { QueryClient } from '@tanstack/react-query';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { reportQuery } from '@/shared/api/workReports';
import { WORK_SUMMARY_KEY } from '@/shared/api/workSummary';
import { formatLocale, setFormatLocale } from '@/shared/format/format';
import { backend, button, mount, refusal, report, sites, toast, types } from './ReportForm.testSupport';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { displayName: 'Иванов' } }) }));

it('показывает подсказку по ответу сервера и сохраняет введённое поле заказчика', async () => {
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((path, config) => path === '/work/line-details-schema'
    ? Promise.resolve({ data: { fields: [
      { key: 'act', title: 'Номер акта', forAcceptance: true },
      { key: 'other', title: 'Другое поле', forAcceptance: true },
    ], types: [], enumTypes: [], primitiveTypes: [] } })
    : get(path, config));
  backend.current = { ...report, lines: [{ ...report.lines[0], missingFields: [{ key: 'act', title: 'Номер акта' }] }] };
  vi.mocked(apiClient.put).mockImplementationOnce(async (_, input) => {
    expect(input).toMatchObject({ lines: [expect.objectContaining({ details: { act: 'А-17' } })] });
    backend.current = { ...backend.current, version: '2', lines: [{ ...backend.current.lines[0], details: { act: 'А-17' }, missingFields: [] }] };
    return { data: backend.current };
  });
  mount('r1');
  const marked = await screen.findByRole('textbox', { name: 'Номер акта' }, { timeout: 5000 });
  const other = screen.getByRole('textbox', { name: 'Другое поле' });
  expect(document.getElementById(marked.getAttribute('aria-describedby')!)?.textContent).toBe('Нужно к приёмке');
  expect(other.hasAttribute('aria-describedby')).toBe(false);
  fireEvent.change(marked, { target: { value: 'А-17' } });
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(screen.queryByText('Нужно к приёмке')).toBeNull(), { timeout: 3000 });
}, 15000);

it('сохраняет неизменённый дробный объём при правке часов в en-US', async () => {
  const locale = formatLocale();
  setFormatLocale('en-US');
  try {
    backend.current = { ...report, lines: [{ ...report.lines[0], volume: 1.234 }] };
    vi.mocked(apiClient.put).mockResolvedValueOnce({ data: backend.current });
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
  backend.current = { ...report, version: '2', lines: [{ ...report.lines[0], volume: 14 }] };
  fireEvent.click(screen.getByRole('button', { name: 'Перечитать' }));
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { ...backend.current, shift: 'night', version: '3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(apiClient.put).toHaveBeenLastCalledWith('/work/reports/r1',
    expect.objectContaining({ shift: 'night', changeId: expect.any(String),
      lines: [expect.objectContaining({ volume: 14, location: 'Этаж 2' })] }),
    { headers: { 'If-Match': '2' } }));
  const body = vi.mocked(apiClient.put).mock.lastCall?.[1] as { lines: unknown[] };
  expect(body.lines[0]).not.toHaveProperty('photos');
});

it('у вида без вариантов скрывает поле, сбрасывает прежний вариант и предлагает единицу нового вида', async () => {
  backend.current = { ...report, lines: [{ ...report.lines[0], variantId: 'v1' }] };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: backend.current });
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
  fireEvent.click(button('Готово'));
  fireEvent.click(screen.getByRole('checkbox', { name: 'Бригадная работа' }));
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Сергеев' }));
  expect(apiClient.get).toHaveBeenCalledWith('/work/constructions/site-a/crew-candidates',
    expect.objectContaining({ params: { date: report.date } }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7.5' } });
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: backend.current });
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
  backend.current = { ...report, status: 'submitted', version: '2' };
  fireEvent.click(await screen.findByRole('button', { name: 'Перечитать' }));
  expect(await screen.findByText(/Стройка, дата и смена восстановлены/)).toBeTruthy();
  expect(screen.getByRole('tab', { name: 'День' }).getAttribute('aria-selected')).toBe('true');
  expect((screen.getByRole('tab', { name: 'Ночь' }) as HTMLButtonElement).disabled).toBe(true);
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: backend.current });
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

it.each(['Сохранить', 'Подать отчёт'])('после действия «%s» обновляет кэш сводки менеджера', async action => {
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: 30_000 } } });
  const summaryKey = [...WORK_SUMMARY_KEY, { from: report.date, to: report.date, offset: 0 }];
  client.setQueryData(summaryKey, { rows: [] });
  mount(report.id, 'entry', client);
  const submit = action === 'Подать отчёт';
  const write = vi.mocked(submit ? apiClient.post : apiClient.put);
  write.mockResolvedValueOnce({ data: { ...report, version: '2', status: submit ? 'submitted' : 'draft', hours: 7 } });
  const control = await screen.findByRole('button', { name: action });
  if (!submit) fireEvent.change(screen.getByRole('textbox', { name: 'Часы за смену' }), { target: { value: '7' } });

  fireEvent.click(control);
  await waitFor(() => expect(client.getQueryState(summaryKey)?.isInvalidated).toBe(true));
});

it('показывает причину пустого ответа справочника материалов', async () => {
  backend.materialReason = 'Справочник номенклатуры не заведён.';
  mount('r1');
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить материал' }));
  fireEvent.click(screen.getByRole('tab', { name: 'Справочник' }));
  expect(await screen.findByText(backend.materialReason)).toBeTruthy();
});

it('при 422 раскрывает вторую строку и переводит фокус к полю материалов', async () => {
  backend.current = { ...report, lines: [report.lines[0], { ...report.lines[0], id: 'l2', photos: [] }] };
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
  fireEvent.click(button('Добавить материал'));
  fireEvent.click(await within(await screen.findByRole('dialog')).findByRole('option', { name: 'Кабель ВВГ' }));
  fireEvent.click(button('Готово'));
  expect(screen.queryByText('Добавьте материал для подачи отчёта.')).toBeNull();
});

const catalogPath = '/work/entry/sites/site-a/catalog/work-types';
const catalogKey = (version = report.version) =>
  ['work', 'entry-catalog', report.constructionId, 'work-types', report.date, report.id, version, ''];
const missingLocation = { line: 1, lineId: 'l1', field: 'location' } as const;
const missingMaterials = { line: 1, lineId: 'l1', field: 'materials' } as const;
const locationError = 'Укажите место работ для подачи отчёта.';
const materialsError = 'Добавьте материал для подачи отчёта.';
const locationField = () => screen.getByRole('textbox', { name: 'Место работ' });
const catalogClient = () => new QueryClient({ defaultOptions: { queries: { staleTime: 30_000 } } });

it('в режиме ввода показывает ошибки и адресный фокус только по 422, пока признаки вида загружаются', async () => {
  const missingSecondLocation = { ...missingLocation, line: 2, lineId: 'l2' };
  backend.current = { ...report, status: 'returned', requiredMissing: [missingLocation, missingMaterials],
    lines: [report.lines[0], { ...report.lines[0], id: 'l2', photos: [] }] };
  const client = catalogClient();
  const stale = { ...types[0], requiresLocation: false, requiresMaterials: false };
  client.setQueryData(catalogKey(), { items: [], selected: [stale] });
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  let finishCatalog!: (value: Awaited<ReturnType<typeof apiClient.get>>) => void;
  const catalogReply = new Promise<Awaited<ReturnType<typeof apiClient.get>>>(resolve => { finishCatalog = resolve; });
  vi.mocked(apiClient.get).mockImplementation((path, config) => path === catalogPath ? catalogReply : get(path, config));
  vi.mocked(apiClient.post).mockRejectedValueOnce(refusal(422, {
    error: 'Отчёт не подан.',
    requiredMissing: [missingSecondLocation, missingMaterials],
  }));
  mount(report.id, 'entry', client);
  await screen.findByRole('button', { name: 'Подать отчёт' });
  await waitFor(() => expect(client.getQueryState(catalogKey())?.fetchStatus).toBe('fetching'));
  expect(screen.queryByText(locationError)).toBeNull();
  expect(screen.queryByText(materialsError)).toBeNull();
  expect(screen.queryByRole('button', { name: /^Строка 1 · / })).toBeNull();
  expect(document.activeElement).toBe(document.body);

  fireEvent.click(button('Подать отчёт'));
  await screen.findByText(locationError);
  expect(vi.mocked(apiClient.post).mock.calls[0][0]).toBe('/work/entry/reports/r1/submit');
  expect(button('Подробности строки 2').getAttribute('aria-expanded')).toBe('true');
  expect(document.activeElement).toBe(locationField());
  expect(screen.queryByText(materialsError)).toBeNull();

  await act(async () => finishCatalog({ data: { items: [], selected: [stale] } }));
  expect(screen.getByText(locationError)).toBeTruthy();
  fireEvent.change(locationField(), { target: { value: 'Участок Б' } });
  expect(screen.queryByText(locationError)).toBeNull();
  fireEvent.click(button('Строка 1 · Материалы'));
  await screen.findByText(materialsError);
  expect(button('Подробности строки 1').getAttribute('aria-expanded')).toBe('true');
  expect(document.activeElement).toBe(button('Добавить материал'));

  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: {
    ...backend.current, version: '2', lines: [report.lines[0], { ...report.lines[0], id: 'l2', location: 'Участок Б', photos: [] }],
  } });
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(screen.queryByText(materialsError)).toBeNull());
  expect(screen.queryByRole('button', { name: 'Строка 1 · Материалы' })).toBeNull();
});

it.each([true, false])('перечитывает выбранный вид из свежего кэша при открытии возвращённого отчёта (материалы: %s)',
  async requiresMaterials => {
    backend.current = { ...report, status: 'returned', requiredMissing: [missingLocation, missingMaterials] };
    const client = catalogClient();
    client.setQueryData(reportQuery('own', report.id).queryKey, backend.current);
    client.setQueryData(catalogKey(), { items: types });
    const get = vi.mocked(apiClient.get).getMockImplementation()!;
    vi.mocked(apiClient.get).mockImplementation((path, config) => path === catalogPath
      ? Promise.resolve({ data: { items: [types[1]], selected: [{ ...types[0], requiresLocation: false, requiresMaterials }] } })
      : get(path, config));
    mount(report.id, 'own', client);
    expect(screen.getByText('Нужно для ИД · место работ, материалы')).toBeTruthy();
    expect(screen.queryByText(locationError)).toBeNull();
    expect(screen.queryByText(materialsError)).toBeNull();
    expect(document.activeElement).toBe(document.body);
    await waitFor(() => expect(screen.queryByText('Нужно для ИД · место работ, материалы')).toBeNull());
    expect(vi.mocked(apiClient.get).mock.calls.filter(([path]) => path === catalogPath)).toHaveLength(1);
    if (requiresMaterials) {
      expect(screen.getByText('Нужно для ИД · материалы')).toBeTruthy();
    } else {
      expect(screen.queryByText(/Нужно для ИД/)).toBeNull();
      vi.mocked(apiClient.post).mockResolvedValueOnce({ data: {
        ...backend.current, version: '2', status: 'submitted', submittedBy: 'manual', requiredMissing: null,
      } });
      fireEvent.click(button('Подать отчёт'));
      await screen.findByText('Подан');
      expect(apiClient.post).toHaveBeenCalledExactlyOnceWith('/work/reports/r1/submit',
        expect.objectContaining({ changeId: expect.any(String) }), { headers: { 'If-Match': report.version } });
      expect(apiClient.put).not.toHaveBeenCalled();
    }
  });

it('перечитывает справочник, когда фоновый ответ открывает возвращённую версию со свежим кэшем видов', async () => {
  const client = catalogClient();
  const returned = { ...report, status: 'returned', version: '2', requiredMissing: [missingLocation] };
  client.setQueryData(reportQuery('own', report.id).queryKey, { ...report, status: 'submitted' });
  client.setQueryData(catalogKey(), { items: types });
  client.setQueryData(catalogKey(returned.version), { items: [], selected: [types[0]] });
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  let finishReport!: (value: Awaited<ReturnType<typeof apiClient.get>>) => void;
  const reportReply = new Promise<Awaited<ReturnType<typeof apiClient.get>>>(resolve => { finishReport = resolve; });
  vi.mocked(apiClient.get).mockImplementation((path, config) => {
    if (path === '/work/reports/r1') return reportReply;
    if (path === catalogPath) return Promise.resolve({ data: {
      items: [], selected: [{ ...types[0], requiresLocation: false }],
    } });
    return get(path, config);
  });
  mount(report.id, 'own', client);
  expect(screen.getByText('Подан')).toBeTruthy();
  expect(screen.getByText('Нужно для ИД · место работ, материалы')).toBeTruthy();
  expect(apiClient.get).not.toHaveBeenCalledWith(catalogPath, expect.anything());

  await act(async () => finishReport({ data: returned }));
  expect(await screen.findByText('Нужно для ИД · материалы')).toBeTruthy();
  expect(screen.getByText('Возвращён')).toBeTruthy();
  expect(screen.queryByText(locationError)).toBeNull();
  expect(vi.mocked(apiClient.get).mock.calls.filter(([path]) => path === catalogPath)).toHaveLength(1);
});

it('после 409 просит заново подтвердить удаление строки, к которой успели добавить фото', async () => {
  backend.current = { ...report, lines: [{ ...report.lines[0], photos: [] }] };
  const withPhoto = { ...report, version: '2' };
  vi.mocked(apiClient.put).mockRejectedValueOnce(refusal(409, { error: 'Отчёт тем временем изменили.' }));
  mount('r1');
  fireEvent.click(await screen.findByRole('button', { name: 'Удалить строку 1' }));
  fireEvent.click(button('Сохранить'));
  backend.current = withPhoto;
  fireEvent.click(await screen.findByRole('button', { name: 'Перечитать' }));
  expect(await screen.findByText(/подтвердите удаление заново/)).toBeTruthy();
  fireEvent.click(button('Удалить строку 1'));
  expect(await screen.findByText(/фото \(1\)/)).toBeTruthy();
});

it('в поданном отчёте оставляет шапку запертой и сохраняет правку строки с версией', async () => {
  backend.current = { ...report, status: 'submitted' };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: backend.current });
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
  backend.current = { ...report, status: 'returned', version: '2', returnComment: 'Уточните дату смены.' };
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
  backend.current = { ...report, status: 'returned', version: '2' };
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
  backend.current = { ...report, id: attempt.id, shift: 'night' };
  vi.mocked(apiClient.post).mockResolvedValueOnce({ data: backend.current });
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
    backend.current = { ...backend.current, date, shift: 'day', hours: 7, version: '2' };
    return { data: backend.current };
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
