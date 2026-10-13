// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import type { AppUser } from '@/shared/api/users';
import { ResetPasswordModal } from './UsersPage';

/**
 * Сброс пароля (issue #1237): принятый сервером пароль остаётся на экране, пока человек сам не
 * скажет «Готово».
 *
 * До правки окно закрывалось само через 1,2 секунды и стирало пароль: администратор, не успевший его
 * прочитать, сбрасывал второй раз.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });
const PASSWORD = 'Новый-Пароль-2026';
const user = { id: 'u1', email: 'ivanov@example.test', displayName: 'Иванов', roles: [] } as unknown as AppUser;

async function issued() {
  vi.mocked(apiClient.post).mockResolvedValue({ data: null });
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <ResetPasswordModal user={user} onClose={onClose} />
    </QueryClientProvider>,
  );
  fireEvent.change(screen.getByLabelText(/^Новый пароль/), { target: { value: PASSWORD } });
  fireEvent.click(screen.getByRole('button', { name: 'Сбросить пароль' }));
  await tick();
  await tick();
  return onClose;
}

describe('сброс пароля', () => {
  it('после успеха окно не закрывается само, и пароль остаётся на экране', async () => {
    const onClose = await issued();
    // Дольше прежнего таймера (1,2 с) — настоящим временем: подменённые часы не увидели бы таймер,
    // заведённый до подмены, и проверка молчала бы при любом коде.
    await act(async () => { await new Promise(r => setTimeout(r, 1500)); });

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('status', { name: 'Новый пароль' }).textContent).toBe(PASSWORD);
  });

  it('закрывает только «Готово» — ни промах, ни Esc, ни крестик', async () => {
    const onClose = await issued();
    fireEvent.pointerDown(document.body);
    fireEvent.click(document.body);
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    await tick();

    expect(onClose).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Закрыть' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Готово' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('«Скопировать» кладёт в буфер тот самый пароль, а отказ буфера называет', async () => {
    const writeText = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('нет доступа'));
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await issued();

    fireEvent.click(screen.getByRole('button', { name: 'Скопировать' }));
    await tick();
    expect(writeText).toHaveBeenCalledWith(PASSWORD);
    expect(screen.getByRole('button', { name: 'Скопировано' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Скопировано' }));
    await tick();
    expect(screen.getByRole('alert').textContent).toContain('Скопировать не удалось');
  });
});
