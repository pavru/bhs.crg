// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import type { ModuleSettings } from '@/shared/api/moduleSettings';
import { ModuleSettingsSections } from './ModuleSettingsSections';

/**
 * Предупреждение числами (E2, issue #1100, spec v3.3 п. 1.9): окно подтверждения смены допуска говорит,
 * что смена сделает с записанными счетами, — словами модуля из `POST …/preview`. Пока считается — так и
 * сказано; отказ предпросмотра — отказ на экране, а не пустое место: молчание читалось бы «ничего не тронет».
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

afterEach(() => { cleanup(); vi.clearAllMocks(); localStorage.clear(); });

const COSTS: ModuleSettings = {
  code: 'costs', title: 'Счета и накладные', mapping: null,
  settings: [{
    key: 'costs.allocation.tolerance', title: 'Допуск расхождения сумм', effect: 'Что меняет',
    kind: 'number', value: '1.00', stored: null, stale: null, default: '1.00',
    min: 0, max: 100, scale: 2, unit: null, money: true, changeWarning: 'Действует на все счета.', impact: true,
  }],
};

const PENDING = 'Считаем, что изменится…';

const TOLD = '2 счёта заперты закрытым периодом или уже исправлялись: смена запишет книгу исправлений.';

/** Секция модуля открыта, допуск набран, «Сохранить» нажато — окно подтверждения на экране. */
async function confirming(preview: () => Promise<unknown>, module: ModuleSettings = COSTS) {
  localStorage.setItem('crg.section.module.costs', '1');
  vi.mocked(apiClient.get).mockResolvedValue({ data: { modules: [module] } });
  vi.mocked(apiClient.post).mockImplementation(preview as never);
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <ToastProvider><ModuleSettingsSections /></ToastProvider>
    </QueryClientProvider>,
  );
  fireEvent.change(await screen.findByLabelText('Допуск расхождения сумм'), { target: { value: '0,50' } });
  await reopen();
}

async function reopen() {
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await screen.findByText('Действует на все счета.');
}

const told = (text = TOLD) => Promise.resolve({ data: { impacts: [
  { key: 'costs.allocation.tolerance', title: 'Допуск', changed: 5, recorded: 2, text },
] } });

describe('окно подтверждения смены настройки модуля', () => {
  it('показывает числа модуля под предупреждением, спросив тем же телом, что уйдёт в запись', async () => {
    await confirming(() => told());

    expect(await screen.findByText(TOLD)).toBeTruthy();
    expect(apiClient.post).toHaveBeenCalledWith('/settings/modules/costs/preview',
      { values: { 'costs.allocation.tolerance': '0.50' } });
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  it('пока считается — говорит об этом', async () => {
    await confirming(() => new Promise(() => {}));
    expect(await screen.findByText(PENDING)).toBeTruthy();
  });

  it('отказ предпросмотра — отказ на экране, а сохранить всё равно можно', async () => {
    await confirming(() => Promise.reject({ response: { status: 500, data: {} } }));
    expect(await screen.findByText('Не удалось посчитать, что смена сделает с уже записанными данными.')).toBeTruthy();

    vi.mocked(apiClient.put).mockResolvedValue({ data: null });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить' }));
    await waitFor(() => expect(apiClient.put).toHaveBeenCalled());
    expect(apiClient.put).toHaveBeenCalledWith('/settings/modules/costs',
      { values: { 'costs.allocation.tolerance': '0.50' } });
  });

  // Ревью PR #1381: числа — о данных этой минуты. Повторно открытое окно не показывает прошлый ответ
  // уверенным тоном, пока считается новый.
  it('повторно открытое окно спрашивает заново, а не показывает прошлые числа', async () => {
    await confirming(() => told());
    await screen.findByText(TOLD);
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await waitFor(() => expect(screen.queryByText(TOLD)).toBeNull());

    vi.mocked(apiClient.post).mockImplementation((() => new Promise(() => {})) as never);
    await reopen();
    expect(await screen.findByText(PENDING)).toBeTruthy();
    expect(screen.queryByText(TOLD)).toBeNull();
    expect(apiClient.post).toHaveBeenCalledTimes(2);
  });

  // Ревью PR #1381: сохранение не перезапрашивает числа уже после записи — сервер ответил бы «ничего не
  // меняется», и строка пропала бы из открытого окна.
  it('сохранение не спрашивает числа второй раз', async () => {
    await confirming(() => told());
    await screen.findByText(TOLD);
    vi.mocked(apiClient.put).mockResolvedValue({ data: null });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить' }));
    await waitFor(() => expect(apiClient.put).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText('Действует на все счета.')).toBeNull());
    expect(apiClient.post).toHaveBeenCalledTimes(1);
  });

  // Ревью PR #1381: знаки после запятой проверяет только сервер. Его отказ по полю — под полем, как местный:
  // подтверждать значение, которое запись отвергнет, незачем.
  it('отказ предпросмотра по полю закрывает окно и встаёт под полем', async () => {
    await confirming(() => Promise.reject({ response: { status: 400, data: {
      error: 'Настройки не сохранены', fields: { 'costs.allocation.tolerance': 'не больше 2 знаков после запятой' },
    } } }));
    expect(await screen.findByText('Не больше 2 знаков после запятой')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Действует на все счета.')).toBeNull());
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  // Ревью PR #1381: у настройки без обработчика модуля чисел не бывает — и обещать их незачем.
  it('настройка, чей модуль чисел не называет, их не спрашивает и не обещает', async () => {
    await confirming(() => told(), { ...COSTS, settings: [{ ...COSTS.settings[0], impact: false }] });
    expect(screen.queryByText(PENDING)).toBeNull();
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  // Ревью PR #1381: ответ без этой настройки — не молчание: сервер не видит смены, и это сказано словами.
  it('ответ без этой настройки сказан словами, а не пустым местом', async () => {
    await confirming(() => Promise.resolve({ data: { impacts: [] } }));
    expect(await screen.findByText(/действующее значение не меняется/)).toBeTruthy();
  });
});
