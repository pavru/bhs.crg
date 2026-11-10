import { createContext, useContext, useSyncExternalStore } from 'react';
import type { QueryClient } from '@tanstack/react-query';
import { onInvoiceWritten, type InvoiceView } from '@/shared/api/invoices';
import { formatDate } from '@/shared/format/format';
import { K, asInput } from './invoiceFields';
import { deleteRecord } from './recordDelete';

/**
 * Удаление счёта — правила экрана (issue #1282; раздел Дизайнера от 11.10.2026).
 *
 * <p>Удаляется только черновик без оплаты — насовсем, вместе с файлом. Здесь лежит всё, что экран
 * решает и говорит об этом сам: когда действие есть, как назвать счёт и чем кончилась попытка.</p>
 */

// Слово состояния живёт с остальными словами сервера (`invoiceFields`); здесь — для прежних потребителей.
import { DRAFT } from './invoiceFields';
export { DRAFT };

/**
 * Можно ли удалить — то же условие, что проверит сервер. Действие, которое сервер заведомо откажется
 * выполнить, не показывается вовсе (правило экрана): у разобранного, отклонённого и оплаченного пункта
 * нет, а путь к удалению называет руководство. У черновика с книгой исправлений — тоже нет: сервер
 * отвечает ему 409 (`InvoiceDeletion`), книга закрытого периода насовсем не удаляется
 * (costs-invoice-screen-10-layout, задача 2.4).
 */
export const canDelete = (view: InvoiceView, canEdit: boolean): boolean =>
  canEdit && view.requisites[K.state] === DRAFT && !view.payment.paid && (view.book ?? 0) === 0;

/** Имя файла счёта; `null` — файл не приложен. */
export function fileNameOf(view: InvoiceView): string | null {
  const node = view.requisites[K.scan];
  if (node == null || typeof node !== 'object') return null;
  const name = (node as Record<string, unknown>).fileName;
  return typeof name === 'string' && name ? name : null;
}

/** «Счёт № 123 от 01.10.2026» / «Счёт без номера» — в заголовке диалога и в сообщении об исходе. */
export function invoiceTitle(view: InvoiceView): string {
  const number = asInput(view.requisites[K.number]).trim();
  if (!number) return 'счёт без номера';
  const date = asInput(view.requisites[K.date]).slice(0, 10);
  return `счёт № ${number}${date ? ` от ${formatDate(date)}` : ''}`;
}

/**
 * Кнопка подтверждения называет то, что удаляется, — не голое «Удалить». У счёта без номера опознать
 * его можно только по файлу; нет и файла — это пустой черновик, и так и сказано.
 */
export function confirmLabel(view: InvoiceView): string {
  const number = asInput(view.requisites[K.number]).trim();
  if (number) return `Удалить счёт № ${number}`;
  const file = fileNameOf(view);
  return file ? `Удалить счёт из файла „${file}“` : 'Удалить пустой черновик';
}

/** Что уйдёт вместе со счётом — только существующее: пустой перечень пугал бы тем, чего нет. */
export function goingAway(view: InvoiceView): string[] {
  const gone: string[] = [];
  if (view.lines.length > 0) gone.push(`строки счёта — ${view.lines.length}`);
  if (view.lines.some(line => line.allocation.parts.length > 0)) gone.push('разноска по объектам и статьям');
  const file = fileNameOf(view);
  if (file) gone.push(`файл счёта «${file}» и прочитанное из него`);
  return gone;
}

export type { DeleteOutcome } from './recordDelete';

/**
 * Удалить счёт — и перевести отказы на язык экрана. Разбор общий с накладной: см. `deleteRecord`.
 * У счёта «изменили» бывает и без человека: его файл дочитался между открытием диалога и нажатием.
 *
 * @param send сам запрос удаления.
 * @param readFresh прочитать счёт заново, мимо кэша.
 */
export const deleteInvoice = (send: () => Promise<unknown>, readFresh: () => Promise<InvoiceView>) =>
  deleteRecord(send, readFresh, fresh => canDelete(fresh, true), {
    unknown: 'Удалён ли счёт — неизвестно: ответ не пришёл. Посмотрите список счетов.',
    changed: 'Счёт тем временем изменили — или дочитали его файл. Он не удалён: закройте это окно, '
      + 'посмотрите счёт заново и решите ещё раз.',
    noLonger,
  });

/**
 * То же для отложенного удаления нового черновика (D-13.4): разбор отказов тот же, слова — снэкбара, окна нет.
 * Свежепрочитанный на 409 счёт ложится в кэш: повторное удаление уйдёт уже с его версией, а не упрётся в
 * ту же старую (ревью PR #1808).
 */
export const deleteDraftLater = (send: () => Promise<unknown>, readFresh: () => Promise<InvoiceView>) =>
  deleteRecord(send, readFresh, fresh => canDelete(fresh, true), {
    unknown: 'Удалён ли черновик — неизвестно: ответ не пришёл. Список счетов перечитан — посмотрите его.',
    changed: 'Черновик не удалён: его тем временем изменили — или дочитали его файл. Посмотрите его и удалите '
      + 'ещё раз.',
    noLonger,
  });

function noLonger(fresh: InvoiceView): string {
  return fresh.payment.paid
    ? 'Счёт тем временем оплатили. Он не удалён: удаляется только черновик без оплаты — сначала '
      + 'отмените оплату.'
    : `Счёт тем временем изменили: теперь он — ${String(fresh.requisites[K.state]).toLowerCase()}. Он не `
      + 'удалён: удаляется только черновик — верните счёт в черновик.';
}

/** Откуда пришли к удалению: от этого зависит первая строка диалога. */
export type DeleteReason = 'menu' | 'duplicate' | 'same-file' | 'wrong-file';

/**
 * Удаление открытого счёта для входов (меню «Ещё», пункты перечня). `null` — удалить нельзя: входы в этом
 * случае действие не показывают.
 */
export interface InvoiceDeleteEntry {
  /** Удалить: окном — или сразу, отложенным запросом, если `now`. */
  ask: (reason: DeleteReason) => void;
  /**
   * Новый черновик удаляется без окна (D-13.5): у входа подпись без многоточия. Кроме дубликата — его
   * узнают по сохранённым реквизитам, значит, черновик правили, и вопрос остаётся окном (`deletesNow`).
   */
  now: boolean;
}

/** Без окна ли удаляет этот вход — по таблице D-13.5. */
export const deletesNow = (entry: InvoiceDeleteEntry | null, reason: DeleteReason): boolean =>
  !!entry?.now && reason !== 'duplicate';

export const InvoiceDeleteContext = createContext<InvoiceDeleteEntry | null>(null);

export const useInvoiceDelete = () => useContext(InvoiceDeleteContext);

// ── «Свежий» черновик (costs-invoice-screen-13-undo, D-13.3) ────────────────────────────────────────────

/**
 * Черновики, заведённые из файла В ЭТОЙ ВКЛАДКЕ, в которые с тех пор из неё ничего не записано. Такой
 * удаляется без окна (D-13.5): терять в нём нечего, кроме прочитанного из файла, — «не тот файл».
 *
 * <p>Живёт в памяти вкладки и с перезагрузкой пропадает — безопасное направление: окно спросит лишний раз.
 * Чужой черновик и черновик прошлого сеанса сюда не попадают никогда.</p>
 */
const createdHere = new Set<string>();
const freshListeners = new Set<() => void>();
const freshChanged = () => freshListeners.forEach(listener => listener());

/** Черновик заведён из файла этой вкладкой — при ответе заведения. */
export function markCreatedHere(id: string): void {
  createdHere.add(id);
  freshChanged();
}

/** Записали из этой вкладки — черновик больше не «свежий». */
export function forgetFresh(id: string): void {
  if (createdHere.delete(id)) freshChanged();
}

export const isFresh = (id: string): boolean => createdHere.has(id);

export function useFresh(id: string): boolean {
  return useSyncExternalStore(subscribeFresh, () => createdHere.has(id), () => false);
}

function subscribeFresh(listener: () => void) {
  freshListeners.add(listener);
  return () => { freshListeners.delete(listener); };
}

const watched = new WeakSet<QueryClient>();

/**
 * Любая своя запись счёта снимает признак — подпиской `onInvoiceWritten`: через неё идёт каждая запись
 * этой вкладки (реквизиты, строки, «Всё верно», разноска, переходы), и новый пишущий хук попадёт сюда сам.
 * Сдвиг версии распознаванием записью человека не считается — он сюда не приходит. Подписка одна на
 * клиент запросов и не снимается: черновик могут править на любом экране.
 */
export function watchFreshness(qc: QueryClient): void {
  if (watched.has(qc)) return;
  watched.add(qc);
  onInvoiceWritten(qc, (_before, after) => forgetFresh(after.id));
}

/** Для тестов: забыть всё. */
export function resetFresh(): void {
  createdHere.clear();
  freshChanged();
}
