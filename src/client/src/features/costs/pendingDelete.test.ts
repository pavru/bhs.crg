// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import {
  pendingIds, resetPendingDeletes, schedule, send, settle, undo, usePendingDeletes, type DeletePort,
} from './pendingDelete';

/** Отложенное удаление нового черновика (costs-invoice-screen-13-undo, D-13.4, задачи 3.2 и 3.3). */

afterEach(resetPendingDeletes);

const refusal = (status: number) => Object.assign(new Error('отказ'), { response: { status } });

function port(over: Partial<DeletePort> = {}): DeletePort & Record<keyof DeletePort, ReturnType<typeof vi.fn>> {
  return {
    send: vi.fn(async () => 'deleted'), deleted: vi.fn(), refused: vi.fn(), beacon: vi.fn(), reread: vi.fn(), ...over,
  } as never;
}

describe('pendingDelete', () => {
  it('«Вернуть» — DELETE не вызывался, черновик снова в списке', () => {
    const p = port();
    const hidden = renderHook(() => usePendingDeletes());
    act(() => { schedule('i1', 'v3', p); });
    expect(hidden.result.current.has('i1')).toBe(true);
    act(() => { expect(undo('i1')).toBe(true); });
    settle('i1', 'action');
    expect(p.send).not.toHaveBeenCalled();
    expect(hidden.result.current.has('i1')).toBe(false);
  });

  it('закрытие снэкбара — один DELETE с версией, которую человек видел; черновик выброшен из кэша', async () => {
    const p = port();
    schedule('i1', 'v3', p);
    settle('i1', 'timeout');
    settle('i1', 'dismissed');
    await vi.waitFor(() => expect(p.deleted).toHaveBeenCalledWith('i1', 'deleted'));
    expect(p.send.mock.calls).toEqual([['i1', 'v3']]);
  });

  it('смахнули и отброшено очередью — тоже закрытие: удаление уходит', async () => {
    const a = port();
    const b = port();
    schedule('a', 'v1', a);
    schedule('b', 'v1', b);
    settle('a', 'dismissed');
    settle('b', 'dropped');
    await vi.waitFor(() => expect(b.deleted).toHaveBeenCalled());
    expect(a.send).toHaveBeenCalledTimes(1);
  });

  it('409 — отказ назван, и черновик снова в списке; автоповтора нет', async () => {
    const error = refusal(409);
    const p = port({ send: vi.fn(async () => { throw error; }) });
    const hidden = renderHook(() => usePendingDeletes());
    act(() => { schedule('i1', 'v3', p); });
    await act(async () => { await send('i1'); });
    expect(p.refused).toHaveBeenCalledWith('i1', error);
    expect(p.deleted).not.toHaveBeenCalled();
    expect(hidden.result.current.has('i1')).toBe(false);
    expect(p.send).toHaveBeenCalledTimes(1);
  });

  it('двойной щелчок — второй schedule ничего не делает', () => {
    const p = port();
    expect(schedule('i1', 'v3', p)).toBe(true);
    expect(schedule('i1', 'v4', p)).toBe(false);
  });

  it('pagehide — ждущие уходят запросом при закрытии, и поздний отклик закрытия второго не шлёт', async () => {
    const p = port();
    schedule('i1', 'v3', p);
    schedule('i2', 'v5', p);
    undo('i2');
    window.dispatchEvent(new Event('pagehide'));
    expect(p.beacon.mock.calls).toEqual([['i1', 'v3']]);
    settle('i1', 'timeout');
    await Promise.resolve();
    expect(p.send).not.toHaveBeenCalled();
    // «Вернуть» после ухода запроса ничего не возвращает — и говорит это, а не молчит (у вызывающего).
    expect(undo('i1')).toBe(false);
  });

  // Ревью PR #1808: удалённый не мигает строкой до перечитывания списка — кэш чистится, пока он ещё скрыт.
  it('удалён — сначала из кэша, потом из скрытых; «уже удалён» передаётся исходом', async () => {
    const seen: boolean[] = [];
    const p = port({ send: vi.fn(async () => 'already' as const), deleted: vi.fn(() => { seen.push(pendingIds().has('i1')); }) });
    schedule('i1', 'v3', p);
    await send('i1');
    expect(p.deleted).toHaveBeenCalledWith('i1', 'already');
    expect(seen).toEqual([true]);
    expect(pendingIds().has('i1')).toBe(false);
  });

  // Ревью PR #1808: `pagehide` бывает и при уходе со страницы, которая потом вернётся из кэша истории.
  it('страница вернулась из кэша истории — ушедшее по pagehide не прячется навсегда, а перечитывается', () => {
    const p = port();
    schedule('i1', 'v3', p);
    window.dispatchEvent(new Event('pagehide'));
    window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: true }));
    expect(pendingIds().has('i1')).toBe(false);
    expect(p.reread.mock.calls).toEqual([['i1']]);
    expect(p.send).not.toHaveBeenCalled();
  });

  it('обычная загрузка страницы (не из кэша истории) ждущих не трогает', () => {
    const p = port();
    schedule('i1', 'v3', p);
    window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: false }));
    expect(pendingIds().has('i1')).toBe(true);
    expect(p.reread).not.toHaveBeenCalled();
  });
});
