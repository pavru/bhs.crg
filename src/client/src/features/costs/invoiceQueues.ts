import type { InvoiceListItem, InvoiceQueue, InvoiceReferencePlace } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';

/**
 * Очереди списка счетов (issue #1186): «Разобрать», «Удалённые записи», «В архиве» — чипами над
 * списком, выбор один. Здесь — чистая логика: что написано на чипе, что в строке и что в пустом списке.
 */

/** Подписи чипов. Короче, чем у готовых отборов реестра: рейл — 320 пикселей. */
export const QUEUE_LABEL: Record<InvoiceQueue, string> = {
  parsing: 'Разобрать',
  unrecognized: 'Не распознано',
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

/** Сколько пунктов «чего не хватает» помещается в строку списка: рейл — 320 пикселей. */
const READINESS_SHOWN = 2;

/** Третья строчка счёта в списке: чего не хватает до «Разобран». */
export interface RowReadiness {
  text: string;
  /** Полный перечень — подсказкой: в строку помещается не всё. */
  title: string;
  /** Всё готово, осталось нажать «Разобран». */
  ready: boolean;
}

/**
 * Что строка списка говорит о готовности счёта (issue #1292) — чтобы из списка было видно, за какой
 * счёт браться, не открывая каждый. Перечень строит СЕРВЕР, той же функцией, что в открытом счёте:
 * здесь только сколько из него показать.
 *
 * `null` — сказать нечего: счёт не черновик, заперт либо причины не пришли. ⚠️ «Не пришли» не
 * превращается в «готов»: пустой перечень и отсутствующий — разное.
 */
export function rowReadiness(item: InvoiceListItem): RowReadiness | null {
  const gaps = item.readiness;
  if (gaps == null) return null;
  if (gaps.length === 0)
    return { text: 'готов к «Разобран»', title: 'Все условия выполнены — осталось нажать «Разобран».', ready: true };

  const rest = gaps.length - READINESS_SHOWN;
  return {
    text: `осталось: ${gaps.slice(0, READINESS_SHOWN).join(' · ')}${rest > 0 ? ` · ещё ${rest}` : ''}`,
    title: `До «Разобран» осталось: ${gaps.join(' · ')}.`,
    ready: false,
  };
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
  /**
   * След счёта, выведенного из очереди отсюда же (issue #1293): с него перешли к следующему, и строка —
   * путь назад. Здесь причина ухода известна — человек только что нажал «Разобран» либо отклонил
   * счёт, — поэтому слово названо прямо.
   */
  exit?: QueueExit;
}

/** Чем счёт вывели из очереди «Разобрать» на этом экране. Удаления здесь нет нарочно: возвращаться
 *  некуда, а следующий счёт с тем же «Удалить» под рукой провоцирует удаление по инерции. */
export type QueueExit = 'parsed' | 'rejected';

/** Слово о счёте, вышедшем из очереди, — одно в сообщении и в строке-следе. */
export const EXIT_WORD: Record<QueueExit, string> = { parsed: 'разобран', rejected: 'отклонён' };

/** След вышедшего из очереди счёта: где стоял, каким был и чем вышел. */
export type QueueTrace = HeldRow & { how: QueueExit };

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
export function queueRows(listed: InvoiceListItem[], held: HeldRow | null, trace: QueueTrace | null = null): QueueRow[] {
  const rows: QueueRow[] = listed.map(item => ({ item, left: false }));
  const absent = (row: HeldRow | null): row is HeldRow => !!row && !listed.some(i => i.id === row.item.id);
  // След раньше открытого счёта: оба стояли в одном списке, и вставка по возрастанию места сохраняет
  // их взаимный порядок. Открыли сам след — строка одна, и она остаётся следом.
  const extra = [
    ...(absent(trace) ? [{ at: trace.index, row: { item: trace.item, left: true, exit: trace.how } }] : []),
    ...(absent(held) && held.item.id !== trace?.item.id ? [{ at: held.index, row: { item: held.item, left: true } }] : []),
  ].sort((a, b) => a.at - b.at);
  for (const { at, row } of extra) rows.splice(Math.min(at, rows.length), 0, row);
  return rows;
}

/** Как счёт называют в сообщении: номером, а без номера — так и сказано. */
const called = (number: string | null | undefined) => number?.trim() ? `Счёт № ${number.trim()}` : 'Счёт без номера';

/** Что делать после выхода счёта из очереди под её отбором — и какими словами об этом сказать. */
export interface AfterExit {
  /** Счёт, который открыть следом; `null` — остаться на прежнем. */
  open: InvoiceListItem | null;
  message: string;
}

/**
 * Куда ведёт «Разобран» — и «Отклонить» (issue #1293). Под отбором «Разобрать» открывается следующая ВИДИМАЯ строка —
 * та, что стоит под разобранной в списке на момент нажатия, уже с поиском: человек работает с тем,
 * что видит. С последней строки — на первую. Вне отбора — `null`: счёт остаётся открытым, слов нет.
 *
 * <p>Переход не делается, пока в форме есть несохранённое: смена счёта перемонтирует форму, и правки
 * пропали бы молча. Счёт при этом разобран — об этом и сказано.</p>
 *
 * <p>⚠️ Список, который не прочитан, — не пустая очередь (ревью PR #1303): `rows === null` отвечает
 * «следующий не открыт», а не «разбирать больше нечего».</p>
 *
 * <p>⚠️ «Готовых» вперёд не ставим и запертые не пропускаем: порядок списка предсказуем, прыжки —
 * нет, а строка без причины значит и «заперт», и «причина не прочитана» — клиент их не различает.</p>
 *
 * @param rows строки списка, перечитанного ПОСЛЕ перехода, с разобранным счётом на прежнем месте
 *   (удержанной строкой) и уже с поиском; `null` — список не прочитан.
 * @param number номер разобранного счёта — у открытого счёта, строка списка могла устареть.
 */
export function afterExit(
  queue: InvoiceQueue | null, rows: QueueRow[] | null, id: string, number: string | null, query: string, unsaved: boolean,
  how: QueueExit = 'parsed',
): AfterExit | null {
  if (queue !== 'parsing') return null;
  const done = `${called(number)} ${EXIT_WORD[how]}.`;
  if (unsaved) return { open: null, message: `${done} Следующий не открыт: в форме несохранённые правки.` };
  if (!rows) return { open: null, message: `${done} Следующий не открыт: список счетов не прочитан.` };

  // Очередь — живые строки отбора: удержанные и след уже не черновики.
  const live = rows.filter(r => !r.left || r.item.id === id);
  const at = live.findIndex(r => r.item.id === id);
  // Разобранного в списке нет вовсе (поиск его скрыл) — идём с начала.
  const others = at < 0 ? live : [...live.slice(at + 1), ...live.slice(0, at)];
  if (others.length === 0) {
    const text = query.trim();
    return {
      open: null,
      message: text ? `${done} По запросу «${text}» в отборе больше ничего нет.` : `${done} Очередь «Разобрать» пуста.`,
    };
  }
  const next = others[0].item;
  return {
    open: next,
    message: next.number?.trim() ? `${done} Открыт следующий — № ${next.number.trim()}.` : `${done} Открыт следующий.`,
  };
}

/** «Вернуться» не выполнено: в открытом счёте набрано несохранённое, и уводить с него нельзя. */
export function backRefused(number: string | null, how: QueueExit = 'parsed'): string {
  return `${called(number)} не открыт: в форме несохранённые правки. Он стоит в списке строкой «${EXIT_WORD[how]}».`;
}

/**
 * Держать ли след разобранного счёта. Нет — когда отбор сменили или сняли и когда счёт вернулся в
 * список (его вернули в черновик): дальше он может уйти из очереди по любой другой причине —
 * отклонён, удалён, — и слово «разобран» стало бы неправдой (ревью PR #1303).
 */
export function keepTrace(trace: HeldRow | null, queue: InvoiceQueue | null, listed: InvoiceListItem[] | undefined): boolean {
  return !!trace && trace.queue === queue && !listed?.some(i => i.id === trace.item.id);
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
      return 'Разбирать нечего: черновиков нет — все счета разобраны либо отклонены.';
    case 'unrecognized':
      return queues
        ? 'Нераспознанных черновиков нет: у каждого счёта с файлом есть строки либо распознавание ещё идёт.'
        : 'Нераспознанных черновиков не найдено. Сколько их всего — не посчитано.';
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
