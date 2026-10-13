// @vitest-environment jsdom
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import { ReopenDialog } from './periods/PeriodsPage';
import { CancelDialog } from './costs/InvoicePayment';

/**
 * Диалоги операций (issue #1236): пока запрос не завершён, окно не закрывается НИ ОДНИМ выходом, а
 * отказ сервера показан в том же окне.
 *
 * До правки обработчик был один — `if (!o) onDone()`, а «Отмена» в подвале оставалась живой во время
 * запроса: окно исчезало, и отказ (409 «период тем временем изменили») человек не видел вовсе.
 * Запрос подменён здесь на обещание, которое тест держит в руках: «идёт запрос» — это состояние, в
 * котором тест может постоять, а не миг между двумя строками.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

function held() {
  let reject!: (e: unknown) => void;
  const promise = new Promise<never>((_, no) => { reject = no; });
  vi.mocked(apiClient.post).mockReturnValue(promise);
  return { refuse: (error: string) => reject({ response: { status: 409, data: { error } } }) };
}

const shown = (dialog: ReactElement) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
    <ToastProvider>{dialog}</ToastProvider>
  </QueryClientProvider>,
);

const tick = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

/** Все выходы подряд: щелчок мимо, Esc, крестик и кнопка отказа от операции. */
async function tryEveryExit(decline: string) {
  await tick();
  fireEvent.pointerDown(document.body);
  fireEvent.click(document.body);
  fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
  await tick();
  fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
  fireEvent.click(screen.getByRole('button', { name: decline }));
  await tick();
}

const CASES = [
  {
    name: 'отмена закрытия периода',
    confirm: 'Отменить закрытие',
    decline: 'Не отменять',
    reason: 'Причина отмены',
    dialog: (onDone: () => void) => (
      <ReopenDialog onDone={onDone} name="Компания" nameOf={() => 'Компания'}
        state={{ constructionId: null, closedThrough: '2026-09-30', reopenable: { from: '2026-09-01', through: '2026-09-30', keptClosed: [] } } as never} />
    ),
  },
  {
    name: 'отмена оплаты счёта',
    confirm: 'Отменить оплату',
    decline: 'Не отменять',
    reason: 'Причина отмены',
    dialog: (onDone: () => void) => (
      <CancelDialog onDone={onDone} view={{ id: 'i1', version: '7', requisites: {} } as never} />
    ),
  },
];

describe.each(CASES)('$name', ({ confirm, decline, reason, dialog }) => {
  async function started() {
    const request = held();
    const onDone = vi.fn();
    shown(dialog(onDone));
    fireEvent.change(screen.getByLabelText(new RegExp(`^${reason}`)), { target: { value: 'ошиблись датой' } });
    fireEvent.click(screen.getByRole('button', { name: confirm }));
    await tick();
    expect(apiClient.post).toHaveBeenCalledTimes(1);
    return { request, onDone };
  }

  it('пока запрос идёт, окно не закрывает ни один выход', async () => {
    const { onDone } = await started();
    await tryEveryExit(decline);
    expect(onDone).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('отказ сервера показан в том же окне, и после него выход снова жив', async () => {
    const { request, onDone } = await started();
    await tryEveryExit(decline);
    request.refuse('Тем временем всё изменили.');
    await tick();
    expect(screen.getByRole('alert').textContent).toContain('Тем временем всё изменили.');
    expect(onDone).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: decline }));
    // В окне набрана причина, но вызывающий о правках молчит — выход закрывает без вопроса.
    expect(onDone).toHaveBeenCalledTimes(1);
  });
});
