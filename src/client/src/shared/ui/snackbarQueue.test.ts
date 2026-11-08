import { describe, expect, it } from 'vitest';
import type { SnackbarOptions } from './Snackbar';
import { EMPTY_QUEUE, PENDING_MAX, snackbarKey, snackbarQueue, type SnackbarItem, type SnackbarQueue } from './snackbarQueue';

let id = 0;
const item = (o: SnackbarOptions, at = 0): SnackbarItem => ({ ...o, id: ++id, at, key: snackbarKey(o) });
const push = (s: SnackbarQueue, o: SnackbarOptions, at = 0) => snackbarQueue(s, { type: 'push', item: item(o, at) });
/** Закрыть видимое и показать следующее — как провайдер после анимации выхода. */
const advance = (s: SnackbarQueue, now = 0) =>
  snackbarQueue(snackbarQueue(s, { type: 'close', id: s.shown!.id }), { type: 'next', now });
const shownText = (s: SnackbarQueue) => s.shown?.message ?? null;

describe('очередь уведомлений [UI-71]', () => {
  it('видно одно; второе — после закрытия первого', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сохранено', variant: 'success' });
    s = push(s, { message: 'Отправлено', variant: 'success' });
    expect(shownText(s)).toBe('Сохранено');
    expect(s.pending.map(p => p.message)).toEqual(['Отправлено']);
    expect(shownText(advance(s))).toBe('Отправлено');
  });

  it('видимое не вытесняется даже ошибкой', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сохранено', variant: 'success' });
    s = push(s, { message: 'Сбой', variant: 'error' });
    expect(shownText(s)).toBe('Сохранено');
  });

  it('порядок ожидающих: ошибки, затем со своим действием, затем по приходу', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сохранено', variant: 'success' });
    s = push(s, { message: 'Отправлено', variant: 'success' });
    s = push(s, { message: 'Счёт удалён', variant: 'info', action: { label: 'Вернуться', onClick: () => {} } });
    s = push(s, { message: 'Сбой 1', variant: 'error' });
    s = push(s, { message: 'Сбой 2', variant: 'error' });
    const order: string[] = [];
    while (s.shown) { s = advance(s); if (s.shown) order.push(s.shown.message); }
    expect(order).toEqual(['Сбой 1', 'Сбой 2', 'Счёт удалён', 'Отправлено']);
  });

  it('ожидающий успех старше 15 с отбрасывается, ошибка — нет', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сбой', variant: 'error' }, 0);
    s = push(s, { message: 'Сохранено', variant: 'success' }, 1000);
    s = push(s, { message: 'Сбой 2', variant: 'error' }, 1000);
    s = advance(s, 16_001);
    expect(shownText(s)).toBe('Сбой 2');
    expect(s.pending).toEqual([]);
  });

  it('успех, прождавший меньше 15 с, показывается', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сбой', variant: 'error' }, 0);
    s = push(s, { message: 'Сохранено', variant: 'success' }, 1000);
    expect(shownText(advance(s, 16_000))).toBe('Сохранено');
  });

  it('повтор видимого или ожидающего не ставится; другой текст или вариант — ставится', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сохранено', variant: 'success' });
    s = push(s, { message: 'Сохранено', variant: 'success' });
    expect(s.pending).toEqual([]);
    s = push(s, { message: 'Сбой', variant: 'error', traceId: 't1' });
    s = push(s, { message: 'Сбой', variant: 'error', traceId: 't1' });
    s = push(s, { message: 'Сбой', variant: 'error', traceId: 't2' });
    s = push(s, { message: 'Сохранено', variant: 'info' });
    expect(s.pending.map(p => [p.message, p.traceId ?? p.variant])).toEqual([['Сбой', 't1'], ['Сбой', 't2'], ['Сохранено', 'info']]);
  });

  it('уходящее — уже не видимое: такое же после него ставится', () => {
    let s = push(EMPTY_QUEUE, { message: 'Сохранено', variant: 'success' });
    s = snackbarQueue(s, { type: 'close', id: s.shown!.id });
    s = push(s, { message: 'Сохранено', variant: 'success' });
    expect(s.pending).toHaveLength(1);
  });

  it(`ожидающих не больше ${PENDING_MAX}: уходит самый старый не-ошибка`, () => {
    let s = push(EMPTY_QUEUE, { message: 'видимое' });
    s = push(s, { message: 'ошибка', variant: 'error' });
    for (let i = 1; i <= PENDING_MAX; i++) s = push(s, { message: `сведение ${i}` });
    expect(s.pending).toHaveLength(PENDING_MAX);
    expect(s.pending[0].message).toBe('ошибка');
    expect(s.pending.map(p => p.message)).not.toContain('сведение 1');
  });

  describe('отброшенное с откликом считается закрытым (Э5)', () => {
    const onClose = () => {};

    it('с откликом не стареет, как ошибка: прождав больше 15 с, показывается', () => {
      let s = push(EMPTY_QUEUE, { message: 'Сбой', variant: 'error' }, 0);
      s = push(s, { message: 'Черновик удалён', variant: 'success', action: { label: 'Вернуть', onClick: () => {} }, onClose }, 1000);
      s = push(s, { message: 'Сохранено', variant: 'success' }, 1000);
      s = advance(s, 60_000);
      expect(shownText(s)).toBe('Черновик удалён');
      expect(s.pending).toEqual([]);
      expect(s.dropped).toEqual([]);
    });

    it('переполнение вытесняет сведение раньше уведомления с откликом, хоть оно и старше', () => {
      let s = push(EMPTY_QUEUE, { message: 'видимое' });
      s = push(s, { message: 'Черновик удалён', onClose });
      s = push(s, { message: 'Вернуться', action: { label: 'Вернуться', onClick: () => {} } });
      for (let i = 1; i <= PENDING_MAX - 1; i++) s = push(s, { message: `сведение ${i}` });
      expect(s.pending.map(p => p.message)).not.toContain('сведение 1');
      expect(s.pending.map(p => p.message)).toContain('Черновик удалён');
      expect(s.dropped).toEqual([]);
    });

    it('при одних ошибках и откликах вытесняется самое старое с откликом — в dropped', () => {
      let s = push(EMPTY_QUEUE, { message: 'видимое' });
      s = push(s, { message: 'отклик 0', onClose });
      for (let i = 1; i <= PENDING_MAX - 1; i++) s = push(s, { message: `сбой ${i}`, variant: 'error' });
      s = push(s, { message: 'отклик 1', onClose });
      expect(s.pending.map(p => p.message)).not.toContain('отклик 0');
      expect(s.dropped.map(d => d.message)).toEqual(['отклик 0']);
    });

    it('повтор с откликом ставится — и при видимом, и при ожидающем', () => {
      const o = { message: 'Строка 7 удалена', variant: 'success' as const, action: { label: 'Вернуть', onClick: () => {} }, onClose };
      let s = push(EMPTY_QUEUE, o);
      s = push(s, o);
      s = push(s, o);
      expect(s.pending).toHaveLength(2);
    });

    it('flush убирает названных и только их', () => {
      let s = push(EMPTY_QUEUE, { message: 'видимое' });
      for (let i = 1; i <= PENDING_MAX; i++) s = push(s, { message: `сбой ${i}`, variant: 'error' });
      s = push(s, { message: 'а', onClose });
      s = push(s, { message: 'б', onClose });
      s = push(s, { message: 'в', onClose });
      // Ожидают одни ошибки — каждое с откликом вытесняется сразу, придя.
      expect(s.dropped.map(d => d.message)).toEqual(['а', 'б', 'в']);
      const [a] = s.dropped;
      s = snackbarQueue(s, { type: 'flush', ids: [a.id] });
      expect(s.dropped.map(d => d.message)).toEqual(['б', 'в']);
      expect(snackbarQueue(s, { type: 'flush', ids: [a.id] })).toBe(s);
    });
  });

  it('запоздавший сдвиг без закрытия не снимает видимое', () => {
    const s = push(EMPTY_QUEUE, { message: 'Сохранено' });
    expect(snackbarQueue(s, { type: 'next', now: 0 })).toBe(s);
  });
});
