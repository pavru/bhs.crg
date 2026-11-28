// @vitest-environment jsdom
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import type { ReportInput } from '@/shared/api/workReports';
import { backend, button, currentFromCreate, mount, refusal, report } from './ReportForm.testSupport';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ user: { displayName: 'Иванов' } }) }));

const waitForPhotoLimits = () => screen.findByText('До 5 фото на строку · до 15 МБ на файл', undefined, { timeout: 5000 });

async function pickPhotoForNewReport(file: File) {
  mount('new');
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить строку' }));
  fireEvent.click(button('Вид работы, строка 1'));
  fireEvent.click(await screen.findByRole('option', { name: 'Монтаж коробки' }));
  await waitForPhotoLimits();
  fireEvent.change(screen.getByLabelText('Файлы фото строки'), { target: { files: [file] } });
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
  await waitForPhotoLimits();
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
  const line = backend.current.lines[0];
  expect(apiClient.put).toHaveBeenCalledExactlyOnceWith(`/work/reports/${backend.current.id}/lines/${line.id}/photos/${line.photos[0].id}`,
    file, expect.objectContaining({ headers: { 'Content-Type': 'application/octet-stream' } }));
  expect(screen.queryByRole('heading', { name: 'Новый отчёт' })).toBeNull();
});

it('после потерянного ответа не восстанавливает снятое фото и записывает пустой состав с версией', async () => {
  const file = new File(['фото'], 'снятое.jpg', { type: 'image/jpeg' });
  vi.mocked(apiClient.post).mockImplementationOnce(async (_, input) => {
    currentFromCreate(input);
    throw new Error('Ответ потерян');
  }).mockImplementationOnce(async () => ({ data: backend.current }));
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
    ...backend.current, version: '2', lines: [{ ...backend.current.lines[0], photos: [] }],
  } }));
  fireEvent.click(button('Сохранить'));
  await waitFor(() => expect(apiClient.put).toHaveBeenCalledExactlyOnceWith(`/work/reports/${backend.current.id}`,
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
  await waitForPhotoLimits();
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
  backend.current = { ...report, version: '2' };
  fireEvent.click(button('Перечитать'));
  await waitFor(() => expect(screen.queryByText(/Отчёт тем временем изменили/)).toBeNull());
  vi.mocked(apiClient.delete).mockResolvedValueOnce({ data: { ...backend.current, version: '3', lines: [{ ...backend.current.lines[0], photos: [] }] } });
  fireEvent.click(button('Удалить фото: Фото 1'));
  await screen.findByText('Фото пока нет');
  expect(hours.value).toBe('7');
  expect(apiClient.delete).toHaveBeenLastCalledWith('/work/reports/r1/lines/l1/photos/p1', {
    params: { changeId: expect.any(String) }, headers: { 'If-Match': '2' },
  });
});

it('после закрытия разрешает выбрать файл только для фото, у которого сервер разрешил досылку', async () => {
  backend.current = { ...report, periodClosed: true, status: 'accepted', lines: [{ ...report.lines[0], photos: [
    { id: 'before', state: 'pending', canDeliver: true, originalName: 'досылка.jpg' },
    { id: 'after', state: 'pending', canDeliver: false, originalName: 'исправление.jpg' },
  ] }] };
  vi.mocked(apiClient.put).mockResolvedValueOnce({ data: { state: 'stored' } });
  mount('r1');
  await screen.findByText('Период закрыт.');
  await waitForPhotoLimits();
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
