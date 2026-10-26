import type { AllocationPartView, InvoiceView, LineAllocationView } from '@/shared/api/invoices';
import type { MatrixPart, MatrixState } from '@/shared/api/allocationMatrix';
import type { VatMode } from '@/shared/api/invoices';
import type {
  CorrectionLineView, CorrectionPreview, CorrectionRecordView, InvoiceCorrectionBody, VatModeShift,
} from '@/shared/api/invoiceCorrection';
import { toRequisites } from './invoiceFields';
import { toDrafts, toPayload, type LineDraft } from './invoiceLines';
import { vatModeOf } from './invoiceVat';

/**
 * Самая длинная причина исправления — у счёта и у накладной одна, как у сервера (`CorrectionReason.Length`):
 * длиннее журнал обрезал бы.
 */
export const REASON_LENGTH = 1000;

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
  /** Режим НДС черновика строк (issue #1498); `null` — строки не трогали, режим — как у счёта. */
  vatMode: VatMode | null;
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
  // Строки — в режиме черновика, а нетронутые — в режиме счёта (issue #1498): бумажную сумму сервер
  // принимает только полем режима, и режим едет тем же телом — он обязателен, как строки.
  const mode = content.vatMode ?? vatModeOf(view);
  const drafts = content.lines ?? toDrafts(view.lines, mode);
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

  const payload = toPayload(drafts, mode);
  return {
    reason: content.reason.trim(),
    requisites: toRequisites(view.requisites, content.edits),
    lines: drafts.map((draft, index) => ({ ...payload[index], parts: partsOf(draft) })),
    document,
    vatMode: mode,
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
  return record ? dayRows(record.lines, preview.day, `Днём исправления ${day(preview.day)}`) : [];
}

/**
 * Строки записи двумя группами: легшие днём записи и своим днём (период открыт). Одна функция на
 * предпросмотр и историю счёта — иначе одно исправление до записи и после читалось бы по-разному.
 *
 * <p>Группа — по признаку переноса, а не по дате (ревью PR #1395): своя строка самого дня записи лежит тем же
 * днём, что и перенесённая, и по дате попала бы к ней — две одинаковые строки одной группы, бывает +30 и −30.</p>
 */
export function dayRows(lines: CorrectionLineView[], day: string, today: string): PreviewRow[] {
  const group = (key: string, title: string, part: CorrectionLineView[]): PreviewRow[] => part.length === 0 ? []
    : [{ kind: 'group', key, title }, ...part.map((line, i): PreviewRow => ({ kind: 'line', key: `${key}-${i}`, line }))];
  const moved = (l: CorrectionLineView) => l.shifted ?? l.day === day;
  return [
    ...group('today', today, lines.filter(moved)),
    ...group('own', 'Своим днём (период открыт)', lines.filter(l => !moved(l))),
  ];
}

/** Строки без групп — у открытия книги и выравнивания: там «днём записи» ничего не значит. */
export function plainRows(key: string, lines: CorrectionLineView[]): PreviewRow[] {
  return lines.map((line, i): PreviewRow => ({ kind: 'line', key: `${key}-${i}`, line }));
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

/** Вид записи книги словами истории. Незнакомый (сервер новее клиента) — как пришёл, а не пустотой. */
export function bookKindTitle(kind: string): string {
  switch (kind) {
    case 'opening': return 'Открытие книги';
    case 'correction': return 'Исправление';
    case 'payment': return 'Оплата';
    case 'unpayment': return 'Отмена оплаты';
    case 'alignment': return 'Выравнивание допуском';
    default: return kind;
  }
}

/**
 * Итог записи: у открытия — деньги счёта, как они лежали (сумма, а не разница), у прочих — сдвиг со
 * знаком. Открытие со знаком «+» читалось бы «счёт подорожал на всю сумму».
 */
export function bookTotal(entry: { kind: string; gross: number }, money: (value: number) => string): string {
  return entry.kind === 'opening' ? money(entry.gross) : signed(entry.gross, money);
}

/** Строки записи истории: у записей со сдвигом — группами по дню, у открытия и выравнивания — без групп. */
export function bookEntryRows(entry: { kind: string; seq: number; day: string; lines: CorrectionLineView[] },
  day: (iso: string) => string): PreviewRow[] {
  return entry.kind === 'opening' || entry.kind === 'alignment'
    ? plainRows(`${entry.seq}`, entry.lines)
    : dayRows(entry.lines, entry.day, `Днём записи ${day(entry.day)}`);
}

/**
 * «Кто» у записи истории. У служебной записи (открытие книги, выравнивание внутри чужой записи) имени
 * нет нарочно — её пишет не человек, и прочерк там читался бы «автор потерян».
 */
export function bookAuthor(entry: { kind: string; author: string | null; reason: string | null }): string {
  if (entry.author) return entry.author;
  return entry.kind === 'opening' || (entry.kind === 'alignment' && !entry.reason) ? 'служебная запись' : '—';
}

/** Запись без строк — словами её вида: версия и причина (и режим НДС, если сменился) есть только у исправления. */
export function bookEmpty(kind: string, shift?: VatModeShift | null): string {
  if (kind !== 'correction') return 'строк нет: деньги счёта эта запись не сдвинула.';
  return shift ? 'деньги не менялись: записаны версия, причина и режим НДС.' : 'деньги не менялись: записаны версия и причина.';
}

const PRICES: Record<VatMode, string> = { Included: 'с НДС', OnTop: 'без НДС' };

/**
 * Смена режима НДС отдельным пунктом (issue #1498) — одна строка на предпросмотр и историю. Строками книги её
 * не назвать: при тех же суммах с НДС смена двигает только суммы без НДС, а то и ничего.
 */
export function vatShiftText(shift: VatModeShift): string {
  return `Цены в строках: ${PRICES[shift.from] ?? shift.from} → ${PRICES[shift.to] ?? shift.to}`;
}
