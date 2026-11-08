import { useSyncExternalStore } from 'react';
import type { SnackbarCloseReason } from '@/shared/ui/Snackbar';
import type { DeleteOutcome } from './recordDelete';

/**
 * Отложенное удаление нового черновика (costs-invoice-screen-13-undo, D-13.4, ТЗ COST-6.6).
 *
 * <p>Удаление черновика на сервере — насовсем, вместе с файлом, и восстановить удалённое нечем. Поэтому
 * «Вернуть» возможен только ДО запроса: черновик сразу прячется из списка, а `DELETE` уходит, когда снэкбар
 * «Черновик удалён · Вернуть» закрылся не по «Вернуть» — истёк срок, смахнули, очередь отбросила. «Вернуть»,
 * который ничего не возвращает, был бы отказом, переодетым в результат.</p>
 *
 * <p>Модуль, а не компонент: переход внутри приложения его не размонтирует, и уход по рейлу удаление не
 * отменяет. Закрытие вкладки снэкбар не видит — ждущие уходят по `pagehide` запросом с `keepalive`.</p>
 *
 * <p>Не ушёл запрос — черновик остаётся черновиком: лишний черновик виден, потерянного счёта нет.</p>
 */

/** Чем удаление отправляется и что делать с ответом — даёт страница: у модуля нет ни клиента запросов, ни снэкбара. */
export interface DeletePort {
  /** Сам запрос: `DELETE …/invoices/{id}` с `If-Match`; `already` — счёта уже не было. Отказ — исключением. */
  send: (id: string, version: string) => Promise<DeleteOutcome>;
  /** Счёта больше нет: выбросить его из кэша — и из списков, пока черновик ещё скрыт. */
  deleted: (id: string, outcome: DeleteOutcome) => void;
  /** Отказ: сказать отказом — черновик уже снова в списке. */
  refused: (id: string, error: unknown) => void;
  /** Ушло при закрытии вкладки — `fetch` с `keepalive`: обычный запрос браузер оборвал бы. */
  beacon: (id: string, version: string) => void;
  /** Исход неизвестен (ушло по `pagehide`, а страница вернулась): перечитать, а список скажет сам. */
  reread: (id: string) => void;
}

interface Waiting { version: string; port: DeletePort; sent: boolean; beaconed?: boolean }

const waiting = new Map<string, Waiting>();
const listeners = new Set<() => void>();
let hidden: ReadonlySet<string> = new Set();

function changed() {
  hidden = new Set(waiting.keys());
  listeners.forEach(listener => listener());
}

/** Закрытие вкладки — отправить всё, что ждёт. Слушатель ставится один раз, с первым удалением. */
function onPageHide() {
  for (const [id, item] of waiting) {
    if (item.sent) continue;
    item.sent = true;
    item.beaconed = true;
    try { item.port.beacon(id, item.version); } catch { /* не ушёл — черновик останется черновиком */ }
  }
}

/**
 * Страница вернулась из кэша истории (`pagehide` бывает и при уходе по ссылке, с «Назад» после него):
 * ушедшее по `pagehide` больше не ждёт. Дошёл ли запрос, не знает никто — ответ `keepalive` не читается, а токен
 * мог истечь, — поэтому черновик не прячется навсегда, а перечитывается: удалён — его не будет в списке,
 * нет — он вернётся строкой (ревью PR #1808).
 */
function onPageShow(event: PageTransitionEvent) {
  if (!event.persisted) return;
  const beaconed = [...waiting].filter(([, item]) => item.beaconed);
  if (!beaconed.length) return;
  for (const [id] of beaconed) waiting.delete(id);
  changed();
  for (const [id, item] of beaconed) item.port.reread(id);
}
let listening = false;

/**
 * Отложить удаление. Черновик с этой минуты скрыт. Ждёт он уже — ничего не делает и отвечает `false`:
 * уведомление с откликом не сливается с повтором ([UI-71]), и двойной щелчок дал бы два снэкбара и два
 * запроса.
 *
 * @param version версия, которую человек видел, — она и уйдёт в `If-Match`.
 */
export function schedule(id: string, version: string, port: DeletePort): boolean {
  if (waiting.has(id)) return false;
  waiting.set(id, { version, port, sent: false });
  if (!listening && typeof window !== 'undefined') {
    window.addEventListener('pagehide', onPageHide);
    window.addEventListener('pageshow', onPageShow);
    listening = true;
  }
  changed();
  return true;
}

/** «Вернуть»: удаление отменено, черновик снова в списке. `false` — запрос уже ушёл, и вернуть нечего. */
export function undo(id: string): boolean {
  const item = waiting.get(id);
  if (!item || item.sent) return false;
  waiting.delete(id);
  changed();
  return true;
}

/** Отправить удаление. Ушло раньше (`pagehide`) — второго запроса нет. */
export async function send(id: string): Promise<void> {
  const item = waiting.get(id);
  if (!item || item.sent) return;
  item.sent = true;
  try {
    const outcome = await item.port.send(id, item.version);
    // Сначала из кэша, потом из скрытых: в обратном порядке удалённый мигнул бы строкой до перечитывания.
    item.port.deleted(id, outcome);
    waiting.delete(id);
    changed();
  } catch (error) {
    // Сначала в список, потом отказ: отказ называет черновик, которого иначе не видно.
    waiting.delete(id);
    changed();
    item.port.refused(id, error);
  }
}

/** Отклик закрытия снэкбара «Черновик удалён · Вернуть»: любая причина, кроме «Вернуть», отправляет удаление. */
export function settle(id: string, reason: SnackbarCloseReason): void {
  if (reason !== 'action') void send(id);
}

/**
 * Скрытые сейчас черновики — без подписки, для обработчиков: «следующий» после выхода из очереди не должен
 * оказаться черновиком, который скрыт и вот-вот уйдёт (ревью PR #1808).
 */
export const pendingIds = (): ReadonlySet<string> => hidden;

/** Скрытые сейчас черновики: ждут удаления или ответа на него. */
export function usePendingDeletes(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, () => hidden, () => hidden);
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** Для тестов: забыть всё. */
export function resetPendingDeletes(): void {
  waiting.clear();
  if (listening) {
    window.removeEventListener('pagehide', onPageHide);
    window.removeEventListener('pageshow', onPageShow);
  }
  listening = false;
  changed();
}
