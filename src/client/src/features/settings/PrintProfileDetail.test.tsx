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
        <PrintProfileDetail profile={profile} otherNames={otherNames} onDeleted={() => {}}
          onEditState={() => {}} saveRef={{ current: () => Promise.resolve() }} />
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
    expect((await screen.findByRole('alert')).textContent).toContain('по ширине остаётся 68 мм');
    expect(apiClient.put).not.toHaveBeenCalled();
    // Ошибка пары связана с обоими полями: диктор на поле прочтёт её.
    expect(screen.getByLabelText('Левое').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByLabelText('Правое').getAttribute('aria-describedby')).toBeTruthy();
    // После первой попытки проверка живая: исправили — ошибка ушла, без нового «Сохранить».
    fireEvent.change(screen.getByLabelText('Левое'), { target: { value: '4' } }); // 148 − 4 − 40 = 104
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('смена бумаги пересчитывает остаток, а не оставляет прежний текст', async () => {
    shown(own);
    fireEvent.change(screen.getByLabelText('Левое'), { target: { value: '30' } });
    fireEvent.change(screen.getByLabelText('Правое'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    // A5 книжная: 148 − 30 − 30 = 88.
    expect((await screen.findByRole('alert')).textContent).toContain('остаётся 88 мм');
    fireEvent.click(screen.getByRole('radio', { name: /A4/ }));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('бумага — радиогруппа: одна остановка Tab, стрелка переводит выбор', () => {
    shown(own);
    const group = screen.getByRole('radiogroup', { name: 'Бумага' });
    const a5 = screen.getByRole('radio', { name: /A5/ });
    expect(a5.getAttribute('aria-checked')).toBe('true');
    expect(screen.getAllByRole('radio', { name: /^A/ }).filter(r => r.tabIndex === 0)).toHaveLength(1);
    fireEvent.keyDown(group, { key: 'ArrowRight' });
    expect(screen.getByRole('radio', { name: /A4/ }).getAttribute('aria-checked')).toBe('true');
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
