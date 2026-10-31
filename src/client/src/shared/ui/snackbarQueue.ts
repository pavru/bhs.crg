import type { SnackbarOptions } from './Snackbar';

/**
 * Очередь уведомлений ([UI-71]): видимым — одно, и его ничто не вытесняет. Чистый редьюсер — провайдер
 * держит его в `useReducer`, а тесты проверяют порядок без Radix и таймеров.
 *
 * <p>Порядок ожидающих — ошибки, затем со своим действием, затем по приходу: ошибку с «Сообщить» не
 * должен закрыть следующий «Сохранено», а «Вернуться» — потеряться за сведениями. Ожидающие успех и
 * сведение стареют за 15 с: опрос или фоновая вкладка иначе накопили бы стопку давних «Сохранено».
 * Ошибки не стареют и не отбрасываются — о них человек обязан узнать.</p>
 */

/** Уведомление в очереди: номер, время прихода и ключ повтора. */
export interface SnackbarItem extends SnackbarOptions {
  id: number;
  at: number;
  key: string;
}

export interface SnackbarQueue {
  /** Видимое — или уходящее (`leaving`), пока играет анимация выхода. */
  shown: SnackbarItem | null;
  leaving: boolean;
  pending: SnackbarItem[];
}

export type SnackbarAction =
  | { type: 'push'; item: SnackbarItem }
  | { type: 'close'; id: number }
  | { type: 'next'; now: number };

export const PENDING_MAX = 10;
export const PENDING_TTL_MS = 15_000;

/**
 * Следующее уведомление монтируется после выхода предыдущего: анимация `snackbar-out` — short3 (150 мс),
 * с запасом. Ждём таймером, а не концом анимации: в jsdom и при «меньше движения» её нет вовсе. Здесь,
 * а не у провайдера: модуль компонента, экспортирующий что-то ещё, теряет горячую подмену (#858).
 */
export const SNACKBAR_EXIT_MS = 200;

export const EMPTY_QUEUE: SnackbarQueue = { shown: null, leaving: false, pending: [] };

/** Ключ повтора: двойной щелчок даёт то же уведомление, и второе не ставится. */
export function snackbarKey(o: SnackbarOptions): string {
  return JSON.stringify([o.variant ?? 'info', o.message, o.action?.label ?? null, o.traceId ?? null]);
}

const isError = (i: SnackbarItem) => i.variant === 'error';
const rank = (i: SnackbarItem) => (isError(i) ? 0 : i.action ? 1 : 2);

export function snackbarQueue(state: SnackbarQueue, action: SnackbarAction): SnackbarQueue {
  switch (action.type) {
    case 'push': {
      const { item } = action;
      // Уходящее уже не видно: такое же после него — новое уведомление, а не повтор.
      const visible = state.shown && !state.leaving ? state.shown : null;
      if (visible?.key === item.key || state.pending.some(p => p.key === item.key)) return state;
      if (!state.shown) return { ...state, shown: item };
      const pending = [...state.pending, item];
      while (pending.length > PENDING_MAX) {
        const oldest = pending.findIndex(p => !isError(p));
        if (oldest < 0) break;
        pending.splice(oldest, 1);
      }
      return { ...state, pending };
    }
    case 'close':
      return state.shown?.id === action.id && !state.leaving ? { ...state, leaving: true } : state;
    case 'next': {
      // Сдвиг — только после закрытия: запоздавший таймер прежнего не снимет нынешнее.
      if (!state.leaving) return state;
      const fresh = state.pending.filter(p => isError(p) || action.now - p.at <= PENDING_TTL_MS);
      // Ожидающие лежат по приходу: первый с наименьшим рангом — и самый важный, и самый ранний.
      const best = fresh.reduce<SnackbarItem | null>((b, p) => (!b || rank(p) < rank(b) ? p : b), null);
      return { shown: best, leaving: false, pending: fresh.filter(p => p !== best) };
    }
  }
}
