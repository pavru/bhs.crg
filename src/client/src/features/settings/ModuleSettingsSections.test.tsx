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
    min: 0, max: 100, scale: 2, unit: null, money: true, changeWarning: 'Действует на все счета.',
  }],
};

const TOLD = '2 счёта заперты закрытым периодом или уже исправлялись: смена запишет книгу исправлений.';

/** Секция модуля открыта, допуск набран, «Сохранить» нажато — окно подтверждения на экране. */
async function confirming(preview: () => Promise<unknown>) {
  localStorage.setItem('crg.section.module.costs', '1');
  vi.mocked(apiClient.get).mockResolvedValue({ data: { modules: [COSTS] } });
  vi.mocked(apiClient.post).mockImplementation(preview as never);
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <ToastProvider><ModuleSettingsSections /></ToastProvider>
    </QueryClientProvider>,
  );
  fireEvent.change(await screen.findByLabelText('Допуск расхождения сумм'), { target: { value: '0,50' } });
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await screen.findByText('Действует на все счета.');
}

describe('окно подтверждения смены настройки модуля', () => {
  it('показывает числа модуля под предупреждением, спросив тем же телом, что уйдёт в запись', async () => {
    await confirming(() => Promise.resolve({ data: { impacts: [
      { key: 'costs.allocation.tolerance', title: 'Допуск', changed: 5, recorded: 2, text: TOLD },
    ] } }));

    expect(await screen.findByText(TOLD)).toBeTruthy();
    expect(apiClient.post).toHaveBeenCalledWith('/settings/modules/costs/preview',
      { values: { 'costs.allocation.tolerance': '0.50' } });
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  it('пока считается — говорит об этом', async () => {
    await confirming(() => new Promise(() => {}));
    expect(await screen.findByText('Считаем, что изменится…')).toBeTruthy();
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
});
