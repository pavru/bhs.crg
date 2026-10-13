// @vitest-environment jsdom
import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import { CloseDialog, ReopenDialog } from './periods/PeriodsPage';
import { CancelDialog, PayDialog } from './costs/InvoicePayment';

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

/**
 * Сама операция — обещание в руках теста; предпросмотр (у закрытия периода и оплаты он идёт тем же
 * `post`, по адресу с `/preview`) отвечает сразу — иначе до кнопки операции не дойти.
 */
function held(preview: unknown) {
  let reject!: (e: unknown) => void;
  const promise = new Promise<never>((_, no) => { reject = no; });
  vi.mocked(apiClient.post).mockImplementation((url: string) =>
    url.endsWith('/preview') ? Promise.resolve({ data: preview }) : promise);
  return { refuse: (error: string) => reject({ response: { status: 409, data: { error } } }) };
}

const operations = () => vi.mocked(apiClient.post).mock.calls.filter(([url]) => !String(url).endsWith('/preview'));

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
    name: 'закрытие периода',
    confirm: 'Закрыть период',
    decline: 'Отмена',
    reason: null,
    preview: { stamp: 's1', sections: [] },
    dialog: (onDone: () => void) => (
      <CloseDialog onDone={onDone} name="Компания" today="2026-10-13"
        state={{ constructionId: null, closedThrough: '2026-08-31', expectedFrom: '2026-09-01', reopenable: null } as never} />
    ),
  },
  {
    name: 'оплата счёта',
    confirm: 'Отметить оплату',
    decline: 'Отмена',
    reason: null,
    preview: { today: '2026-10-13', paidOn: '2026-10-13', total: 100, refusal: null, dateRefusal: null, rows: [], stamp: 'p1' },
    dialog: (onDone: () => void) => (
      <PayDialog onDone={onDone} view={{ id: 'i1', version: '7', requisites: {} } as never} />
    ),
  },
  {
    name: 'отмена закрытия периода',
    confirm: 'Отменить закрытие',
    decline: 'Не отменять',
    reason: 'Причина отмены',
    preview: null,
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
    preview: null,
    dialog: (onDone: () => void) => (
      <CancelDialog onDone={onDone} view={{ id: 'i1', version: '7', requisites: {} } as never} />
    ),
  },
];

describe.each(CASES)('$name', ({ confirm, decline, reason, preview, dialog }) => {
  async function started() {
    const request = held(preview);
    const onDone = vi.fn();
    shown(dialog(onDone));
    await tick();   // предпросмотр ответил — кнопка операции ожила
    await tick();
    if (reason)
      fireEvent.change(screen.getByLabelText(new RegExp(`^${reason}`)), { target: { value: 'ошиблись датой' } });
    fireEvent.click(screen.getByRole('button', { name: confirm }));
    await tick();
    expect(operations()).toHaveLength(1);
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
    expect(screen.getAllByRole('alert').map(a => a.textContent).join(' ')).toContain('Тем временем всё изменили.');
    expect(onDone).not.toHaveBeenCalled();

    // Правок, о которых стоило бы спросить, в этих сценариях нет — выход закрывает сразу.
    fireEvent.click(screen.getByRole('button', { name: decline }));
    expect(onDone).toHaveBeenCalledTimes(1);
  });
});
