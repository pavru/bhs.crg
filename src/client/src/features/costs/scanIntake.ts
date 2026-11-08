import type { InvoiceSameFile } from '@/shared/api/invoices';
import type { SameFileAnswer } from '@/shared/api/invoiceSameFile';
import type { FileKindsInfo } from '@/shared/api/fileKinds';
import { FOLDER_REFUSAL } from '@/shared/ui/fileDrop';
import { formatDate, formatDateTime } from '@/shared/format/format';
import { inOrder, precheck } from './scanPrecheck';

/**
 * Бросок до выгрузки (costs-invoice-screen-14, design D3): проверки → отпечатки → вопрос → решение. Одна
 * для одного файла и для пакета — у них одни слова отказа и один вопрос о повторе.
 *
 * <p>Порядок важен. Предпроверка отсекает то, что известно без чтения файла (вид, пустота, размер, папка).
 * Отпечатки считаются только у годных и по одному — в памяти один файл. Повторы внутри броска находятся по
 * отпечаткам ДО сети: заводится первый по {@link inOrder}, остальные названы «выбран дважды». Потом — ОДИН
 * вопрос на бросок (`sameFileByFingerprints`).</p>
 *
 * <p>⚠️ Отказ вопроса — своя ветка, а не `failure()` пакета: там любой отказ, кроме 400 и 413, останавливает
 * загрузку, а проверка повтора — не повод не заводить. Не ответила — заводим, и итог говорит «повтор не
 * проверен». «Файл новый» не говорится никогда: пустой ответ значит «двойников среди проверяемых нет».</p>
 */

export interface CheckPort {
  /** Реестр видов файлов; нет его — вид решает сервер. */
  kinds: FileKindsInfo | undefined;
  /** Отпечаток SHA-256 (`fingerprintOf`). */
  fingerprint: (file: File) => Promise<string>;
  /** Вопрос по отпечаткам (`sameFileByFingerprints`). */
  ask: (prints: string[]) => Promise<SameFileAnswer[]>;
}

/** Файл, который идёт на заведение. `print` — `null`, если отпечаток не посчитался. */
export interface ToSend {
  file: File;
  print: string | null;
}

/** Файл, содержимое которого уже приложено к счетам: до решения человека он не заводится. */
export interface HeldFile {
  file: File;
  print: string;
  twins: InvoiceSameFile[];
}

export interface Checked {
  /** В порядке отправки. */
  send: ToSend[];
  /** Отказы предпроверки и папки — повтор их не исправит. */
  refused: { name: string; reason: string }[];
  held: HeldFile[];
  /** Имена файлов, чьё содержимое уже есть в этом броске под другим именем. */
  doubled: string[];
  /** Вопрос не ответил или хоть один отпечаток не посчитался. */
  unchecked: boolean;
}

/** Проверить бросок. Не бросает: всякий отказ проверки становится `unchecked`, а не отказом заведения. */
export async function checkDrop(files: File[], folders: string[], port: CheckPort): Promise<Checked> {
  const refused = folders.map(name => ({ name, reason: FOLDER_REFUSAL }));
  const good: File[] = [];
  for (const file of inOrder(files)) {
    const problem = precheck(file, port.kinds);
    if (problem) refused.push({ name: file.name, reason: problem });
    else good.push(file);
  }

  let unchecked = false;
  const printed: ToSend[] = [];
  for (const file of good) {
    try { printed.push({ file, print: (await port.fingerprint(file)).toLowerCase() }); }
    catch { printed.push({ file, print: null }); unchecked = true; }
  }

  // Повторы внутри броска: первый по порядку остаётся, остальные — «выбран дважды».
  const seen = new Set<string>();
  const doubled: string[] = [];
  const unique = printed.filter(p => {
    if (p.print === null) return true;
    if (seen.has(p.print)) { doubled.push(p.file.name); return false; }
    seen.add(p.print);
    return true;
  });

  const prints = unique.flatMap(p => (p.print ? [p.print] : []));
  let twinsOf = new Map<string, InvoiceSameFile[]>();
  if (prints.length > 0) {
    try {
      const answers = await port.ask(prints);
      twinsOf = new Map(answers.map(a => [a.fingerprint.toLowerCase(), a.twins]));
      // Ответ без записи о спрошенном отпечатке — «не знаем» о нём, а не «двойников нет».
      if (prints.some(p => !twinsOf.has(p))) unchecked = true;
    } catch { unchecked = true; }
  }

  const send: ToSend[] = [];
  const held: HeldFile[] = [];
  for (const p of unique) {
    const twins = p.print ? twinsOf.get(p.print) ?? [] : [];
    if (p.print && twins.length > 0) held.push({ file: p.file, print: p.print, twins });
    else send.push(p);
  }
  return { send, refused, held, doubled, unchecked };
}

/**
 * «Ответ не пришёл» — а черновик мог завестись (design D3, «мог завестись»). Перед повтором спрашиваем по
 * отпечатку: новый двойник, которого не было до отправки и которого завёл сам читающий, — это свой черновик.
 *
 * @param known двойники файла, известные до отправки (принятый повтор их и имел).
 * @returns свой черновик; `null` — не нашёлся или вопрос не ответил: тогда файл отправляется.
 */
export async function lostDraft(print: string, known: readonly string[], port: Pick<CheckPort, 'ask'>): Promise<string | null> {
  try {
    const [answer] = await port.ask([print]);
    return answer?.twins.find(t => t.mine && !known.includes(t.id))?.id ?? null;
  } catch { return null; }
}

/** Слова итога, когда проверка не ответила (лист «Ф»). */
export const UNCHECKED_NOTE = 'Повтор не проверен — если файл уже был, счёт скажет об этом.';

/** Имя двойника в одну строку: «счёта 2611» или «черновика из файла от 26.10.2026, 12:01». */
export function twinShort(twin: InvoiceSameFile): string {
  return twin.number ? `счёта ${twin.number}` : `черновика из файла от ${formatDateTime(twin.createdAt)}`;
}

/** Заголовок карточки двойника: «Счёт 2611 от 22.10.2026» или «Черновик из файла от … — номер не вписан». */
export function twinTitle(twin: InvoiceSameFile): string {
  if (!twin.number) return `Черновик из файла от ${formatDateTime(twin.createdAt)} — номер не вписан`;
  return `Счёт ${twin.number}${twin.issuedOn ? ` от ${formatDate(twin.issuedOn)}` : ''}`;
}

/** Имя «Открыть …» для диктора: что именно откроется. */
export const twinOpenLabel = (twin: InvoiceSameFile): string =>
  `Открыть ${twin.number ? twinTitle(twin).replace(/^Счёт/, 'счёт') : `черновик из файла от ${formatDateTime(twin.createdAt)}`}`;

/** «заведён 22.10.2026, 09:14 · Потоцкий А.» — без имени строки «кто завёл» нет; свой — «вы». */
export function twinMade(twin: InvoiceSameFile): string {
  const who = twin.mine ? 'вы' : twin.createdByName;
  return `заведён ${formatDateTime(twin.createdAt)}${who ? ` · ${who}` : ''}`;
}

/**
 * Строка придержанного в итоге пакета (лист «Ф»): «уже у счёта 2611 от 22.10.2026 · ООО «…» · черновик», а у
 * черновика без номера — «уже у черновика из файла от 26.10.2026, 12:01 — номер не вписан · завели вы»: что
 * он черновик, сказано самим именем.
 */
export function heldWords(twins: InvoiceSameFile[]): string {
  const [first, ...rest] = twins;
  const parts = first.number
    ? [`уже у ${twinTitle(first).replace(/^Счёт/, 'счёта')}`]
    : [`уже у ${twinShort(first)} — номер не вписан`];
  if (first.supplierName) parts.push(first.supplierName);
  if (first.number || first.state !== 'Черновик') parts.push(first.state.toLowerCase());
  if (first.mine) parts.push('завели вы');
  return parts.join(' · ') + (rest.length > 0 ? ` (и ещё ${rest.length})` : '');
}

/** Разных номеров среди двойников — от двух окно говорит «в файле уже разобрано несколько счетов». */
export const distinctNumbers = (twins: InvoiceSameFile[]): string[] =>
  [...new Set(twins.flatMap(t => (t.number ? [t.number] : [])))];

/** Порог запоздавшего окна — от разбора Дизайнера по листу «Ф». */
export const LATE_MS = 1000;

/** Фокус в поле ввода: окно, всплывшее сейчас, забрало бы Enter у печатающего. */
export function typing(): boolean {
  const el = document.activeElement;
  return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || (el instanceof HTMLElement && el.isContentEditable);
}
