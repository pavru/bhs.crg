// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { REVIEW_CARD_KEY, type ReviewFullCard } from '@/shared/api/workReview';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { ReportCard } from './ReportCard';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));

const initial: ReviewFullCard = {
  id: 'r1', status: 'accepted', employeeName: 'Ирина Иванова', constructionName: 'Терминал', timeZoneId: 'Asia/Vladivostok',
  viewedVersion: 1, atClosingVersion: null, actions: ['correct'], currentRequiredMissing: [],
  workTypes: { w1: 'Прокладка кабеля' }, workVariants: {}, units: { m: 'м' }, sections: {},
  report: { id: 'r1', status: 'accepted', employeeId: 'e1', constructionId: 's1', date: '2026-11-23', shift: 'night',
    periodClosed: true, hours: 8, note: 'Прежнее примечание', version: 'v1', acceptWouldMark: false,
    currentVersion: 1, versions: [], marks: [{ code: 'fields-missing', text: 'не заполнены поля: 1' }],
    lines: [{ id: 'l1', workTypeId: 'w1', variantId: null, volume: 12.5, unitId: 'm', sectionId: null,
      location: 'В щитовой', isCrew: false, details: { legacy: 'Сохранить' }, materials: [{ nomenclatureId: 'mat1', note: 'Прежнее' }],
      participants: [], photos: [{ id: 'p1', state: 'pending' }], comment: 'Комментарий', crewRole: null,
      missingFields: [{ key: 'act', title: 'Номер акта' }] }],
  },
};
const reason = 'Получен акт А-17';
const replaced = { number: 1, reason, author: { name: 'Менеджер' }, at: '2026-11-24T00:00:00Z' };
const error = (status: number, detail = '') => ({ response: { status, data: { error: detail } } });
let current: ReviewFullCard;
let journal: unknown[];

beforeEach(() => {
  vi.resetAllMocks();
  current = initial;
  journal = [];
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.scrollIntoView ??= () => {};
  vi.mocked(apiClient.get).mockImplementation(async (path, config) => {
    if (path === '/account/access') return { data: { permissions: ['work.report.review'], modules: [] } };
    if (path === '/work/line-details-schema') return { data: { fields: [{ key: 'act', title: 'Номер акта', type: 'string' }],
      types: [], enumTypes: [], primitiveTypes: [] } };
    if (path.endsWith('/catalog/work-types')) return { data: { items: [{ id: 'w1', displayName: 'Прокладка кабеля', unitId: 'm', variants: [] }] } };
    if (path.endsWith('/catalog/units')) return { data: { items: [{ id: 'm', name: 'м' }] } };
    if (path.endsWith('/catalog/materials')) return { data: { items: [{ id: 'mat1', displayName: 'Кабель' }] } };
    if (path.endsWith('/catalog/sections')) return { data: { items: [] } };
    if (path.endsWith('/journal')) return { data: { items: journal } };
    if (config?.params?.number === 1 && current.report.currentVersion > 1) return { data: {
      ...initial, actions: [], atClosingVersion: 1, report: { ...initial.report,
        currentVersion: current.report.currentVersion, versions: current.report.versions },
    } };
    return { data: current };
  });
});
afterEach(cleanup);

function mount(query = '') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0 } } });
  render(<QueryClientProvider client={client}><SnackbarProvider><MemoryRouter initialEntries={[`/work-review/r1${query}`]}>
    <NavigationGuardProvider><Routes><Route path="work-review/:reportId" element={<ReportCard />} /></Routes></NavigationGuardProvider>
  </MemoryRouter></SnackbarProvider></QueryClientProvider>);
  return client;
}

const btn = (name: string) => screen.getByRole('button', { name });
const text = (name: string) => screen.getByRole<HTMLInputElement>('textbox', { name });
const change = (name: string, value: string) => fireEvent.change(text(name), { target: { value } });

async function form() {
  fireEvent.click(await screen.findByRole('button', { name: 'Исправить отчёт' }));
  await screen.findByRole('textbox', { name: 'Номер акта' });
  change('Номер акта', 'А-17');
  change('Часы за смену', '6');
  fireEvent.click(btn('Проверить изменения'));
  expect(btn('Сохранить новой версией')).toHaveProperty('disabled', true);
  change('Причина исправления', reason);
}

function saveCurrent(number = 2) {
  current = { ...initial, viewedVersion: number, atClosingVersion: 1, report: { ...initial.report, version: `v${number}`,
    currentVersion: number, versions: [replaced], marks: [], hours: 6,
    lines: [{ ...initial.report.lines[0], details: { legacy: 'Сохранить', act: 'А-17' }, missingFields: [] }],
  } };
  journal = [{ seq: 4, kind: 'corrected', author: replaced.author, executor: replaced.author, enteredFor: false,
    serverTime: replaced.at, deviceTime: null, comment: reason }];
  return { data: current.report };
}

it('пишет полное исправление с исходным If-Match и перечитывает действующую версию и снимок', async () => {
  vi.mocked(apiClient.post).mockImplementation(async () => saveCurrent());
  const client = mount('?number=1');
  const affected = [['work', 'reports', 'own'], ['tables', 'work.reports', 'rows', {}],
    ['tables', 'work.lines', 'rows', {}], ['construction-progress', 's1', null], ['planning', 'schedules', 's1', 'graph']];
  for (const key of affected) client.setQueryData(key, {});
  await form();
  fireEvent.click(btn('Сохранить новой версией'));
  expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
  expect(await screen.findByText('А-17', { exact: true })).toBeTruthy();
  expect(await screen.findByText('Отчёт исправлен после закрытия')).toBeTruthy();
  expect(apiClient.post).toHaveBeenCalledWith('/work/reports/r1/correction', {
    constructionId: 's1', employeeId: 'e1', date: initial.report.date, shift: 'night', hours: 6, note: initial.report.note, reason,
    lines: [{ id: 'l1', workTypeId: 'w1', variantId: null, volume: 12.5, unitId: 'm', sectionId: null, location: 'В щитовой',
      isCrew: false, details: { legacy: 'Сохранить', act: 'А-17' }, materials: [{ nomenclatureId: 'mat1', note: 'Прежнее' }],
      participants: [], comment: 'Комментарий', crewRole: null }],
  }, { headers: { 'If-Match': 'v1' } });
  expect(apiClient.put).not.toHaveBeenCalled();
  for (const key of affected) expect(client.getQueryState(key)?.isInvalidated).toBe(true);
  fireEvent.keyDown(screen.getByRole('combobox', { name: 'Версия отчёта' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name: /Версия 1 · на момент закрытия/ }));
  expect(await screen.findByText('Нужно к приёмке')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Исправить отчёт' })).toBeNull();
  expect(screen.queryByText('А-17', { exact: true })).toBeNull();
});

it('в исправлении выбирает значение из каталога, ограниченного стройкой и отчётом', async () => {
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  const type = { id: 'target', name: 'Протокол', parentId: null, schema: {} };
  const choice = { id: 'record', compositeTypeId: type.id, displayName: 'Протокол стройки', scope: 'Construction', archived: false };
  vi.mocked(apiClient.get).mockImplementation(async (path, config) => {
    if (path === '/work/line-details-schema') return { data: { fields: [{ key: 'protocol', title: 'Протокол', type: 'doc-ref', typeId: type.id }],
      types: [type], enumTypes: [], primitiveTypes: [] } };
    if (path.endsWith('/catalog/field-references')) return { data: { items: [choice], types: [type] } };
    return get(path, config);
  });
  vi.mocked(apiClient.post).mockImplementation(async () => saveCurrent());
  mount();
  fireEvent.click(await screen.findByRole('button', { name: 'Исправить отчёт' }));
  fireEvent.click(await screen.findByRole('button', { name: /Выбрать: Протокол/ }));
  fireEvent.click(await screen.findByRole('option', { name: choice.displayName }));
  fireEvent.click(btn('Проверить изменения'));
  change('Причина исправления', 'Привязан протокол');
  fireEvent.click(btn('Сохранить новой версией'));
  expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
  expect(apiClient.get).toHaveBeenCalledWith('/work/entry/sites/s1/catalog/field-references', expect.objectContaining({
    params: expect.objectContaining({ reportId: 'r1', date: initial.report.date, typeId: type.id }),
  }));
  expect(apiClient.post).toHaveBeenCalledWith('/work/reports/r1/correction', expect.objectContaining({
    lines: [expect.objectContaining({ details: { legacy: 'Сохранить', protocol: {
      $ref: 'catalog', entryId: choice.id, displayName: choice.displayName, scope: choice.scope,
    } } })],
  }), { headers: { 'If-Match': 'v1' } });
});

it('даёт перейти к проверке после загрузки вложения поля заказчика', async () => {
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation(async (path, config) => path === '/work/line-details-schema' ? { data: {
    fields: [{ key: 'attachment', title: 'Вложение', type: 'file' }], types: [], enumTypes: [], primitiveTypes: [],
  } } : get(path, config));
  const attachment = { blobPath: 'blob/act.pdf', fileName: 'act.pdf', size: 15, mimeType: 'application/pdf' };
  let uploadDone!: (response: { data: typeof attachment }) => void;
  vi.mocked(apiClient.post).mockImplementationOnce(() => new Promise(resolve => { uploadDone = resolve; }));
  vi.mocked(apiClient.post).mockImplementationOnce(async () => saveCurrent());
  mount();
  fireEvent.click(await screen.findByRole('button', { name: 'Исправить отчёт' }));
  change('Часы за смену', '6');
  fireEvent.change(await screen.findByLabelText(/Нажмите для выбора файла/), {
    target: { files: [new File(['Акт'], attachment.fileName, { type: attachment.mimeType })] },
  });
  expect(btn('Проверить изменения')).toHaveProperty('disabled', true);
  await act(async () => uploadDone({ data: attachment }));
  await screen.findByText(/act\.pdf/);
  fireEvent.click(btn('Проверить изменения'));
  change('Причина исправления', reason);
  fireEvent.click(btn('Сохранить новой версией'));
  expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenLastCalledWith('/work/reports/r1/correction', expect.objectContaining({
    lines: [expect.objectContaining({ details: { legacy: 'Сохранить', attachment: { ...attachment, $type: 'file' } } })],
  }), { headers: { 'If-Match': 'v1' } });
});

it('сохраняет причину и правку при обновлении карточки и требует выбора спорного поля после 409', async () => {
  vi.mocked(apiClient.post).mockRejectedValueOnce(error(409, 'Отчёт изменили.'));
  const client = mount();
  await form();
  current = { ...initial, viewedVersion: 2, report: { ...initial.report, currentVersion: 2, version: 'v2', hours: 7 } };
  await act(async () => { await client.invalidateQueries({ queryKey: REVIEW_CARD_KEY }); });
  expect(text('Причина исправления').value).toBe(reason);
  fireEvent.click(btn('Сохранить новой версией'));
  fireEvent.click(await screen.findByRole('button', { name: 'Перечитать и сверить' }));
  expect(await screen.findByText('Действующее · 7')).toBeTruthy();
  expect(btn('Подтвердить новую основу')).toHaveProperty('disabled', true);
  expect(apiClient.post).toHaveBeenCalledTimes(1);
  fireEvent.keyDown(screen.getByRole('combobox', { name: 'Часы за смену' }), { key: 'Enter' });
  fireEvent.click(await screen.findByRole('option', { name: 'Оставить действующее значение' }));
  fireEvent.click(btn('Подтвердить новую основу'));
  expect(text('Часы за смену').value).toBe('7');
  expect(text('Номер акта').value).toBe('А-17');
  fireEvent.click(btn('Проверить изменения'));
  expect(text('Причина исправления').value).toBe(reason);
  vi.mocked(apiClient.post).mockImplementation(async () => saveCurrent(3));
  fireEvent.click(btn('Сохранить новой версией'));
  await waitFor(() => expect(apiClient.post).toHaveBeenLastCalledWith('/work/reports/r1/correction',
    expect.objectContaining({ hours: 7, reason }), { headers: { 'If-Match': 'v2' } }));
});

it.each([403, 404, 409, 400])('показывает отказ %s и сохраняет причину', async status => {
  vi.mocked(apiClient.post).mockRejectedValue(error(status, 'Ошибка записи.'));
  mount();
  await form();
  if (status === 409) current = { ...initial, actions: [], report: { ...initial.report, periodClosed: false } };
  fireEvent.click(btn('Сохранить новой версией'));
  await screen.findByText('Введённые данные и причина сохранены в форме.');
  expect(text('Причина исправления').value).toBe(reason);
  if (status === 409) {
    fireEvent.click(btn('Перечитать и сверить'));
    await screen.findByText('Период открыт. Исправление новой версией больше недоступно.');
  }
  expect(btn('Сохранить новой версией')).toHaveProperty('disabled', status !== 400);
  expect(apiClient.post).toHaveBeenCalledTimes(1);
});

it.each([true, false])('при потере ответа сначала проверяет результат, запись состоялась: %s', async committed => {
  vi.mocked(apiClient.post).mockImplementationOnce(async () => { if (committed) saveCurrent(); throw new Error('network'); });
  mount();
  await form();
  const submit = btn('Сохранить новой версией');
  act(() => { fireEvent.click(submit); fireEvent.click(submit); });
  fireEvent.click(await screen.findByRole('button', { name: 'Проверить версию и журнал' }));
  expect(apiClient.post).toHaveBeenCalledTimes(1);
  if (committed) {
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть действующую версию' }));
    expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
    expect(apiClient.post).toHaveBeenCalledTimes(1);
  } else {
    vi.mocked(apiClient.post).mockImplementationOnce(async () => saveCurrent());
    fireEvent.click(await screen.findByRole('button', { name: 'Повторить отправку' }));
    expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
    expect(vi.mocked(apiClient.post).mock.calls[1]).toEqual(vi.mocked(apiClient.post).mock.calls[0]);
  }
});

it('после подтверждённой записи повторяет чтение при отказе GET', async () => {
  const get = vi.mocked(apiClient.get).getMockImplementation()!;
  let readFails = false;
  vi.mocked(apiClient.get).mockImplementation((path, config) => readFails && path === '/work/review/reports/r1'
    ? Promise.reject(error(500, 'Не удалось прочитать карточку.')) : get(path, config));
  vi.mocked(apiClient.post).mockImplementation(async () => { readFails = true; return saveCurrent(); });
  mount();
  await form();
  fireEvent.click(btn('Сохранить новой версией'));
  await screen.findByText('Не удалось прочитать карточку.');
  readFails = false;
  fireEvent.click(btn('Повторить'));
  expect(await screen.findByText('Версия 2 сохранена.')).toBeTruthy();
  expect(apiClient.post).toHaveBeenCalledTimes(1);
});

it('при отмене спрашивает о черновике, а после отбрасывания возвращает фокус к действию', async () => {
  mount();
  await form();
  fireEvent.click(btn('Отмена'));
  const confirm = within(await screen.findByRole('dialog', { name: 'Несохранённые изменения' }));
  expect(confirm.getByText('Введённые изменения и причина исправления не сохранены.')).toBeTruthy();
  fireEvent.click(confirm.getByRole('button', { name: 'Не сохранять' }));
  expect(await screen.findByRole('button', { name: 'Исправить отчёт' })).toBe(document.activeElement);
  expect(apiClient.post).not.toHaveBeenCalled();
});
