import type { InvoiceListItem, InvoiceQueue, InvoiceReferencePlace } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';

/**
 * Очереди списка счетов (issue #1186): «Разобрать», «Удалённые записи», «В архиве» — чипами над
 * списком, выбор один. Здесь — чистая логика: что написано на чипе, что в строке и что в пустом списке.
 */

/** Подписи чипов. Короче, чем у готовых отборов реестра: рейл — 320 пикселей. */
export const QUEUE_LABEL: Record<InvoiceQueue, string> = {
  parsing: 'Разобрать',
  lost: 'Удалённые записи',
  archived: 'В архиве',
};

/** Чип очереди «наведите порядок», как его рисовать. */
export interface QueueChip {
  /** Число при подписи; пусто — числа нет (ноль под нажатым чипом). «?» — проверено не всё. */
  count: string;
  title: string;
  /** Чип говорит о сомнении, а не о числе: проверено не всё. */
  doubt: boolean;
}

/**
 * Чип очереди по числам сервера — или `null`, когда показывать нечего.
 *
 * Ноль не рисуем: чип с нулём звал бы туда, где пусто. Два исключения: нажатый чип остаётся (иначе
 * отбор нечем снять, а его пропажа после последней замены читалась бы как сбой), и «проверено не
 * всё» — ноль с этой оговоркой не значит «нет».
 *
 * @param queues числа; `undefined` — не пришли. Нажатый чип тогда остаётся без числа: «не пришло» —
 * не «ноль», и о несчитанном экран говорит отдельно.
 */
export function queueChip(
  queue: 'lost' | 'archived', queues: InvoiceQueues | undefined, active: boolean,
): QueueChip | null {
  if (!queues)
    return active ? { count: '', title: 'Сколько таких счетов — не посчитано.', doubt: false } : null;
  const number = queues[queue];
  const doubt = queues.doubt !== null;
  if (number === 0 && !doubt && !active) return null;

  const count = number > 0 ? `${number}${doubt ? '?' : ''}` : doubt ? '?' : '';
  const said = [
    queue === 'lost'
      ? `Счета, в которых стоит удалённая запись справочника: ${number}. Откройте счёт и замените значение.`
      : `Неоплаченные счета, в которых выбрана запись из архива: ${number}. Счёт верен; заменить стоит, `
        + 'если запись убрали как дубль.',
    ...(queue === 'lost' && queues.locked > 0 ? [lockedNote(queues.locked)] : []),
    ...(doubt ? [`Число неполное — ${queues.doubt}.`] : []),
  ];
  return { count, title: said.join(' '), doubt };
}

/** О счетах закрытого периода: в отбор они не входят, и молчать о них нельзя. */
export function lockedNote(locked: number): string {
  return `Ещё в счетах закрытого периода (${locked}) стоят удалённые записи. Исправить их нельзя — в отбор они не входят.`;
}

const PLACE: Record<InvoiceReferencePlace['kind'], string> = {
  supplier: 'поставщик',
  payer: 'плательщик',
  type: 'тип счёта',
  position: 'позиции',
  allocation: 'разноска',
  other: 'прочее',
};

/** Сколько ссылок не на месте — число у значка строки. */
export const placesCount = (places: InvoiceReferencePlace[] | undefined) =>
  (places ?? []).reduce((sum, p) => sum + p.count, 0);

/**
 * Где они: «поставщик · позиции: 2 · разноска: 1». Поставщик, плательщик и тип — по одному на счёт,
 * число при них было бы шумом.
 */
export function placesText(places: InvoiceReferencePlace[] | undefined): string {
  const single = new Set<InvoiceReferencePlace['kind']>(['supplier', 'payer', 'type']);
  return (places ?? [])
    .map(p => single.has(p.kind) && p.count === 1 ? PLACE[p.kind] : `${PLACE[p.kind]}: ${p.count}`)
    .join(' · ');
}

/** Подсказка значка «удалённые записи» в строке — с тем, что с этим можно сделать. */
export function lostTitle(item: InvoiceListItem): string {
  const where = `Удалённые записи — ${placesText(item.references?.lost)}.`;
  switch (item.references?.lostState) {
    case 'locked': return `${where} Счёт в закрытом периоде: исправить нельзя, пока закрытие не отменят.`;
    case 'type': return `${where} Заменить тип счёта в форме нечем.`;
    default: return `${where} Откройте счёт и замените значение.`;
  }
}

/** Строка списка: счёт и признак того, что под отбором его уже нет. */
export interface QueueRow {
  item: InvoiceListItem;
  /**
   * Счёт под отбор больше не попадает, но открыт — строка держится до ухода с него.
   * ⚠️ `item` тогда — СНИМОК, каким счёт под отбором стоял: рисовать по нему можно только то, что
   * правкой не меняется. И ПОЧЕМУ счёт ушёл, отсюда не видно: его могли исправить, а могли отклонить
   * или оплатить в закрытый период — слова «исправлено» список сказать не вправе (ревью PR #1241).
   */
  left: boolean;
}

/** Открытый счёт, запомненный под отбором: где стоял и каким был. */
export interface HeldRow {
  queue: InvoiceQueue;
  item: InvoiceListItem;
  index: number;
}

/**
 * Что запомнить об открытом счёте. Счёт под отбором — запоминается (и обновляется: исправили одно
 * место из трёх — строка та же, число другое). Ушли со счёта, сменили или сняли отбор — забывается.
 *
 * Возвращает прежнее значение, когда менять нечего: по этому вызывающий понимает, что писать не надо.
 */
export function heldRow(
  held: HeldRow | null, queue: InvoiceQueue | null, selected: string | null, listed: InvoiceListItem[] | undefined,
): HeldRow | null {
  if (!queue || !selected) return null;
  const kept = held && held.queue === queue && held.item.id === selected ? held : null;
  // Список ещё не пришёл — о счёте ничего не известно: держим, что было.
  if (!listed) return kept;
  const index = listed.findIndex(i => i.id === selected);
  if (index < 0) return kept;
  return kept && kept.item === listed[index] && kept.index === index ? kept : { queue, item: listed[index], index };
}

/**
 * Строки списка под отбором: ответ сервера плюс открытый счёт, если он под отбором БЫЛ. Строка не
 * уходит из-под человека сразу после сохранения — исчезнувший без его действия счёт читается как сбой.
 * Стоит она там же, где стояла.
 */
export function queueRows(listed: InvoiceListItem[], held: HeldRow | null): QueueRow[] {
  const rows = listed.map(item => ({ item, left: false }));
  if (!held || listed.some(i => i.id === held.item.id)) return rows;
  rows.splice(Math.min(held.index, rows.length), 0, { item: held.item, left: true });
  return rows;
}

/**
 * Почему список пуст — словами того отбора, под которым он пуст.
 *
 * @param queues числа сервера; `undefined` — не пришли (идёт запрос либо отказ). ⚠️ Тогда о запертых
 * счетах и о полноте проверки не известно ничего, и сказать «исправлять нечего» было бы отказом,
 * переодетым в ответ (ревью PR #1241).
 */
export function emptyText(queue: InvoiceQueue | null, query: string, queues: InvoiceQueues | undefined): string {
  const text = query.trim();
  if (text)
    return queue ? `В отборе «${QUEUE_LABEL[queue]}» по запросу «${text}» ничего нет.` : 'Ничего не найдено.';
  switch (queue) {
    case 'parsing':
      return 'Разбирать нечего: строк, ждущих позиции номенклатуры, нет ни у одного счёта. '
        + 'Счета без строк вовсе в этот отбор не входят — это другая работа.';
    case 'lost':
      if (!queues)
        return 'Счетов с удалёнными записями, которые можно исправить, не найдено. '
          + 'Есть ли такие в закрытом периоде и всё ли проверено — не посчитано.';
      if (queues.doubt) return `Удалённых записей не найдено, но ${queues.doubt}.`;
      return queues.locked > 0
        ? `Исправлять нечего. В счетах закрытого периода (${queues.locked}) удалённые записи остались — исправить их нельзя.`
        : 'Исправлять нечего: счетов с удалёнными записями нет.';
    case 'archived':
      if (!queues) return 'Неоплаченных счетов с записями из архива не найдено. Всё ли проверено — не посчитано.';
      return queues.doubt
        ? `Счетов с записями из архива не найдено, но ${queues.doubt}.`
        : 'Неоплаченных счетов с записями из архива нет.';
    default: return 'Счетов пока нет.';
  }
}
