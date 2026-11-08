import type { SnackbarOptions } from './Snackbar';

/**
 * Очередь уведомлений ([UI-71]): видимым — одно, и его ничто не вытесняет. Чистый редьюсер — провайдер
 * держит его в `useReducer`, а тесты проверяют порядок без Radix и таймеров.
 *
 * <p>Порядок ожидающих — ошибки, затем со своим действием, затем по приходу: ошибку с «Сообщить» не
 * должен закрыть следующий «Сохранено», а «Вернуться» — потеряться за сведениями. Ожидающие успех и
 * сведение стареют за 15 с: опрос или фоновая вкладка иначе накопили бы стопку давних «Сохранено».
 * Ошибки не стареют и не отбрасываются — о них человек обязан узнать.</p>
 *
 * <p>Уведомление с откликом закрытия держит отложенное действие («Черновик удалён · Вернуть» шлёт
 * удаление по закрытию), поэтому не стареет, как и ошибка: устаревшее выполнило бы действие, «Вернуть»
 * которого человек так и не увидел. Отбросить его может только переполнение — и то последним из
 * не-ошибок. Отброшенное считается закрытым: оно копится в `dropped`, и провайдер зовёт отклик с
 * причиной `dropped`. Звать отсюда нельзя — редьюсер обязан быть чистым, а `StrictMode` зовёт его
 * дважды, и отклик ушёл бы дважды.</p>
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
  /** Отброшенные очередью, не показавшись, — только с `onClose`; провайдер зовёт отклик и `flush`. */
  dropped: SnackbarItem[];
}

export type SnackbarAction =
  | { type: 'push'; item: SnackbarItem }
  | { type: 'close'; id: number }
  | { type: 'next'; now: number }
  | { type: 'flush'; ids: number[] };

export const PENDING_MAX = 10;
export const PENDING_TTL_MS = 15_000;

/**
 * Следующее уведомление монтируется после выхода предыдущего: анимация `snackbar-out` — short3 (150 мс),
 * с запасом. Ждём таймером, а не концом анимации: в jsdom и при «меньше движения» её нет вовсе. Здесь,
 * а не у провайдера: модуль компонента, экспортирующий что-то ещё, теряет горячую подмену (#858).
 */
export const SNACKBAR_EXIT_MS = 200;

export const EMPTY_QUEUE: SnackbarQueue = { shown: null, leaving: false, pending: [], dropped: [] };

/** Ключ повтора: двойной щелчок даёт то же уведомление, и второе не ставится. */
export function snackbarKey(o: SnackbarOptions): string {
  return JSON.stringify([o.variant ?? 'info', o.message, o.action?.label ?? null, o.traceId ?? null]);
}

const isError = (i: SnackbarItem) => i.variant === 'error';
const rank = (i: SnackbarItem) => (isError(i) ? 0 : i.action ? 1 : 2);
/** Цена потери при переполнении: сведение, затем со своим действием, затем с откликом; ошибка — никогда. */
const keep = (i: SnackbarItem) => (i.onClose ? 2 : i.action ? 1 : 0);
/** Кого вытеснить: самого старого из наименее ценных не-ошибок; -1 — одни ошибки. */
const evictee = (pending: SnackbarItem[]) => pending.reduce(
  (at, p, i) => (isError(p) || (at >= 0 && keep(pending[at]) <= keep(p)) ? at : i), -1);
/** Отброшенное без отклика исчезает молча, с откликом — ждёт провайдера. */
const withDropped = (dropped: SnackbarItem[], gone: SnackbarItem[]) => {
  const owed = gone.filter(i => i.onClose);
  return owed.length ? [...dropped, ...owed] : dropped;
};

export function snackbarQueue(state: SnackbarQueue, action: SnackbarAction): SnackbarQueue {
  switch (action.type) {
    case 'push': {
      const { item } = action;
      // Уходящее уже не видно: такое же после него — новое уведомление, а не повтор. Уведомление с
      // откликом повтором не бывает: отклик несёт своё действие (какую строку вернуть), и слитое
      // второе потеряло бы его.
      const visible = state.shown && !state.leaving ? state.shown : null;
      const repeat = !item.onClose && (visible?.key === item.key || state.pending.some(p => p.key === item.key));
      if (repeat) return state;
      if (!state.shown) return { ...state, shown: item };
      const pending = [...state.pending, item];
      const evicted: SnackbarItem[] = [];
      while (pending.length > PENDING_MAX) {
        const at = evictee(pending);
        if (at < 0) break;
        evicted.push(...pending.splice(at, 1));
      }
      return { ...state, pending, dropped: withDropped(state.dropped, evicted) };
    }
    case 'close':
      return state.shown?.id === action.id && !state.leaving ? { ...state, leaving: true } : state;
    case 'next': {
      // Сдвиг — только после закрытия: запоздавший таймер прежнего не снимет нынешнее.
      if (!state.leaving) return state;
      const isFresh = (p: SnackbarItem) => isError(p) || !!p.onClose || action.now - p.at <= PENDING_TTL_MS;
      const fresh = state.pending.filter(isFresh);
      // Ожидающие лежат по приходу: первый с наименьшим рангом — и самый важный, и самый ранний.
      const best = fresh.reduce<SnackbarItem | null>((b, p) => (!b || rank(p) < rank(b) ? p : b), null);
      return {
        shown: best, leaving: false, pending: fresh.filter(p => p !== best),
        dropped: withDropped(state.dropped, state.pending.filter(p => !isFresh(p))),
      };
    }
    case 'flush': {
      const done = new Set(action.ids);
      return state.dropped.some(d => done.has(d.id))
        ? { ...state, dropped: state.dropped.filter(d => !done.has(d.id)) }
        : state;
    }
  }
}
