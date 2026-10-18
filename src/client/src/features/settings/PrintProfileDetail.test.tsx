// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import type { PrintProfile } from '@/shared/api/printProfiles';
import { PrintProfileDetail } from './PrintProfileDetail';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

/**
 * Карточка профиля печати (issue #1455): у профиля по умолчанию нет «Удалить», а «Сбросить к
 * заводским» — только у правленого; негодный черновик на сервер не уходит, отказ с адресом поля
 * встаёт под поле.
 */

afterEach(cleanup);
// В фигурных скобках: функцию, которую вернул beforeEach, vitest зовёт уборкой после теста — а mockReset
// возвращает саму заглушку, и `put` звался бы ещё раз, уже после теста.
beforeEach(() => { vi.mocked(apiClient.put).mockReset(); });

const MARGINS = { top: 5, bottom: 5, left: 5, right: 5 };
const system = (isFactory: boolean): PrintProfile => ({
  id: 's', name: 'по умолчанию', paper: 'A4', orientation: 'portrait', fit: 'width',
  margins: isFactory ? MARGINS : { ...MARGINS, left: 10 }, isSystem: true, isFactory,
  factory: { name: 'по умолчанию', paper: 'A4', orientation: 'portrait', fit: 'width', margins: MARGINS },
});
const own: PrintProfile = {
  id: 'o', name: 'A5 для накладных', paper: 'A5', orientation: 'portrait', fit: 'width', margins: MARGINS,
  isSystem: false, isFactory: false, factory: null,
};

function shown(profile: PrintProfile, otherNames: string[] = []) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ToastProvider>
        <PrintProfileDetail profile={profile} otherNames={otherNames} onDeleted={() => {}} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

const button = (name: string) => screen.queryByRole('button', { name });

describe('карточка профиля печати', () => {
  it('профиль по умолчанию: удалить нечем, название не правится, сброса нет, пока не правили', () => {
    shown(system(true));
    expect(button('Удалить')).toBeNull();
    expect(button('Сбросить к заводским')).toBeNull();
    expect((screen.getByLabelText('Название') as HTMLInputElement).readOnly).toBe(true);
  });

  it('правленый профиль по умолчанию — «изменён» и «Сбросить к заводским»', () => {
    shown(system(false));
    expect(screen.getByText('изменён')).toBeTruthy();
    expect(button('Сбросить к заводским')).toBeTruthy();
    expect(button('Удалить')).toBeNull();
  });

  it('свой профиль — «Удалить», без сброса', () => {
    shown(own);
    expect(button('Удалить')).toBeTruthy();
    expect(button('Сбросить к заводским')).toBeNull();
  });

  it('негодный черновик не уходит на сервер: ошибка — под полями', async () => {
    shown(own, ['Другой']);
    fireEvent.change(screen.getByLabelText('Левое'), { target: { value: '40' } });
    fireEvent.change(screen.getByLabelText('Правое'), { target: { value: '40' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect((await screen.findByRole('alert')).textContent).toContain('остаётся 68 мм');
    expect(apiClient.put).not.toHaveBeenCalled();
  });

  it('отказ сервера с адресом поля встаёт под поле', async () => {
    const taken = () => Object.assign(new Error('409'), {
      response: { status: 409, data: { error: 'занято', details: [{ code: 'taken', path: 'name', message: 'Профиль печати «X» уже есть — выберите другое название.' }] } },
    });
    vi.mocked(apiClient.put).mockImplementation((async () => { throw taken(); }) as never);
    shown(own);
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'X' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(screen.getByText(/уже есть — выберите другое название/)).toBeTruthy());
  });
});
