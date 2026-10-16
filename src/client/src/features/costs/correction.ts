import type { AllocationPartView, InvoiceView, LineAllocationView } from '@/shared/api/invoices';
import type { MatrixPart, MatrixState } from '@/shared/api/allocationMatrix';
import type {
  CorrectionLineView, CorrectionPreview, CorrectionRecordView, InvoiceCorrectionBody,
} from '@/shared/api/invoiceCorrection';
import { toRequisites } from './invoiceFields';
import { toDrafts, toPayload, type LineDraft } from './invoiceLines';

/**
 * Исправление счёта закрытого периода (E2, issue #1100, PR 6b-1) — всё, что не рисование.
 *
 * <p><b>Тело — счёт целиком, а черновик — только тронутое.</b> Человек правит шапку, строки или
 * разноску; нетронутое уезжает таким, каким лежит в счёте. Поэтому у черновика строк и разноски
 * `null` — «не трогали», а не «пусто»: пустой набор строк сервер прочитал бы как «строк нет».</p>
 */
export interface CorrectionContent {
  reason: string;
  /** Правки шапки поверх сохранённых реквизитов. */
  edits: Readonly<Record<string, unknown>>;
  /** Черновик строк; `null` — строки не трогали. */
  lines: readonly LineDraft[] | null;
  /** Разноска из матрицы; `null` — не трогали, части уезжают записанными. */
  allocation: MatrixState | null;
}

/**
 * Записанная часть — в вид присылаемой. У строки с количеством уезжает количество, без него — сумма, и
 * никогда оба (сумму части с количеством считает сервер). Сумма — БЕЗ расхождения со счётом: его
 * добавляет сервер при чтении, а хранится часть без него (та же ловушка, что у матрицы, `cellsOf`).
 */
function sentPart(part: AllocationPartView, mode: LineAllocationView['mode']): MatrixPart {
  const place = { construction: part.constructionId, section: part.sectionId, article: part.articleId };
  return mode === 'quantity'
    ? { ...place, quantity: part.quantity, amount: null }
    : { ...place, quantity: null, amount: part.amount === null ? null : Math.round((part.amount - part.discrepancy) * 100) / 100 };
}

/**
 * Части в том порядке, в каком они записаны. ⚠️ Порядок не косметика: сервер сравнивает присланную
 * разноску с записанной ПОСТРОЧНО и по порядку, и переставленные части прочлись бы «разноска меняется» —
 * то есть отказом 403 тому, у кого нет права разноски, хотя он её не трогал.
 */
const inOrder = (parts: readonly AllocationPartView[]) => [...parts].sort((a, b) => a.ordinal - b.ordinal);

/** Тело исправления: черновик поверх счёта. */
export function correctionBody(view: InvoiceView, content: CorrectionContent): InvoiceCorrectionBody {
  const drafts = content.lines ?? toDrafts(view.lines);
  const stored = new Map(view.lines.map(line => [line.id, line]));

  // Матрица исправления собрана по строкам ЧЕРНОВИКА и зовёт их ключом черновика (у записанной строки
  // ключ — её идентификатор): у новой строки идентификатора ещё нет, а разнести её надо тем же телом.
  const partsOf = (draft: LineDraft): MatrixPart[] => {
    if (content.allocation) return content.allocation.lines.find(l => l.line === draft.key)?.parts ?? [];
    const line = draft.id ? stored.get(draft.id) : undefined;
    return line ? inOrder(line.allocation.parts).map(p => sentPart(p, line.allocation.mode)) : [];
  };

  const document = drafts.length > 0 ? []
    : content.allocation ? content.allocation.document
    : inOrder(view.allocation.document.parts).map(p => sentPart(p, 'amount'));

  const payload = toPayload(drafts);
  return {
    reason: content.reason.trim(),
    requisites: toRequisites(view.requisites, content.edits),
    lines: drafts.map((draft, index) => ({ ...payload[index], parts: partsOf(draft) })),
    document,
  };
}

/** Есть ли что проверять: причина названа и что-то изменено. Причину сервер требует и у предпросмотра. */
export function readyToCheck(content: CorrectionContent): boolean {
  return content.reason.trim() !== ''
    && (Object.keys(content.edits).length > 0 || content.lines !== null || content.allocation !== null);
}

/**
 * Что значит отказ 409 исправления — по СВЕЖЕМУ виду счёта, а не по тексту отказа: тексты для человека,
 * и разбирать их значило бы сломаться на первой же правке формулировки.
 *
 * - `unneeded` — счёт больше не заперт (закрытие отменили): правьте как обычно;
 * - `stale` — версия сменилась: черновик собран по прежнему виду;
 * - `stamp` — версия та же, а предпросмотр устарел (полночь, закрыли период, сменили допуск): пересчитать.
 */
export function conflictKind(fresh: InvoiceView, seen: string): 'unneeded' | 'stale' | 'stamp' {
  if (fresh.payment.lockedBy === null) return 'unneeded';
  return fresh.version !== seen ? 'stale' : 'stamp';
}

/** Строка таблицы предпросмотра: заголовок группы или строка книги. */
export type PreviewRow =
  | { kind: 'group'; key: string; title: string }
  | { kind: 'line'; key: string; line: CorrectionLineView };

/**
 * Строки самого исправления — двумя группами: днём исправления (закрытый период его поправку получает
 * этим днём) и своим днём (период ещё открыт — поправка ложится туда, где лежали деньги). Пустая
 * группа не рисуется.
 */
export function correctionRows(preview: CorrectionPreview, day: (iso: string) => string): PreviewRow[] {
  const record = preview.records.find(r => r.kind === 'correction');
  if (!record) return [];
  const ours = record.lines.filter(l => l.day === preview.day);
  const own = record.lines.filter(l => l.day !== preview.day);
  const group = (key: string, title: string, lines: CorrectionLineView[]): PreviewRow[] => lines.length === 0 ? []
    : [{ kind: 'group', key, title }, ...lines.map((line, i): PreviewRow => ({ kind: 'line', key: `${key}-${i}`, line }))];
  return [
    ...group('today', `Днём исправления ${day(preview.day)}`, ours),
    ...group('own', 'Своим днём (период открыт)', own),
  ];
}

/** Записи книги кроме самого исправления — открытие и выравнивание: показываются свёрнутыми. */
export function serviceRecords(preview: CorrectionPreview): CorrectionRecordView[] {
  return preview.records.filter(r => r.kind !== 'correction');
}

/** Знак у денег явно: «+1 200,00», «−300,00». Поправка без знака читается как сумма, а не как разница. */
export function signed(value: number, money: (value: number) => string): string {
  return value > 0 ? `+${money(value)}` : money(value);
}

/**
 * Показывать ли «Исправить»: счёт требует исправления (сервер назвал его запертым), он ещё есть, и у
 * человека и правка счетов, и право исправления. Без любого из прав кнопки нет вовсе — и о режиме ни
 * слова: путь, который откажет, не предлагают.
 */
export function mayCorrect(view: InvoiceView, permission: (code: string) => boolean, gone: boolean): boolean {
  return view.payment.lockedBy !== null && !gone
    && permission('costs.invoice.edit') && permission('costs.invoice.correct');
}
