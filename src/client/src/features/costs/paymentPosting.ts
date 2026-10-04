import type { PostingRow } from '@/shared/api/invoicePayment';
import { ruCount } from '@/shared/utils/pluralize';
import { formatDate, formatMoney } from './invoiceFields';

/**
 * Расклад оплаты для показа (задача C5, issue #1082). Здесь только раскладка по экрану: суммы, даты и
 * причины переноса считает сервер.
 */

/** Сколько непереносимых строк показывать, прежде чем свернуть остальные в одну. */
export const CALM_ROWS_SHOWN = 5;

export interface PostingSummary {
  /** Переносимые строки — их не сворачивают никогда. */
  moved: PostingRow[];
  /** Остальные: дата платежа и есть учётная. */
  calm: PostingRow[];
  movedAmount: number;
}

export function summarize(rows: readonly PostingRow[]): PostingSummary {
  const moved = rows.filter(r => r.moved);
  return {
    moved,
    calm: rows.filter(r => !r.moved),
    movedAmount: moved.reduce((sum, r) => sum + (r.amount ?? 0), 0),
  };
}

/** «2 стройки», «стройка и остаток» — чем названы переносимые строки в предупреждении. */
export function movedSubject(moved: readonly PostingRow[]): string {
  const sites = moved.filter(r => r.kind === 'construction').length;
  const other = moved.length - sites;
  if (other === 0) return ruCount(sites, 'стройка', 'стройки', 'строек');
  if (sites === 0) return ruCount(other, 'строка', 'строки', 'строк') + ' по компании';
  return `${ruCount(sites, 'стройка', 'стройки', 'строек')} и ${ruCount(other, 'строка', 'строки', 'строк')} по компании`;
}

/** Предупреждение над таблицей: «Переносится: 2 стройки, 140 000,00 из 500 000,00.» */
export function movedWarning(summary: PostingSummary, total: number | null): string | null {
  if (summary.moved.length === 0) return null;
  const of = total === null ? '' : ` из ${formatMoney(total)}`;
  return `Переносится: ${movedSubject(summary.moved)}, ${formatMoney(summary.movedAmount)}${of}.`;
}

/**
 * Подпись кнопки записи. С переносом она ДРУГАЯ нарочно: сменившаяся подпись не даёт нажать по
 * привычке там, где деньги уйдут в другой месяц.
 */
export function payLabel(rows: readonly PostingRow[]): string {
  return rows.some(r => r.moved) ? 'Отметить оплату с переносом' : 'Отметить оплату';
}

/** Свёрнутые спокойные строки одной: «Ещё 7 строк — 360 000,00». */
export function foldedLabel(rows: readonly PostingRow[]): string {
  const amount = rows.reduce((sum, r) => sum + (r.amount ?? 0), 0);
  return `Ещё ${ruCount(rows.length, 'строка', 'строки', 'строк')} — ${formatMoney(amount)}`;
}

/** Тост после оплаты: дата и, если был перенос, с какого дня доли вошли в затраты. */
export function paidToast(paidOn: string, rows: readonly PostingRow[]): string {
  const moved = rows.filter(r => r.moved);
  const base = `Счёт оплачен ${formatDate(paidOn)}.`;
  if (moved.length === 0) return base;

  const days = [...new Set(moved.map(r => r.accountingOn))].sort().map(formatDate).join(', ');
  return `${base} Перенесено: ${movedSubject(moved)} — в затраты с ${days}.`;
}

/**
 * Какие строки расклада изменились относительно прежнего — их подсвечивают после отказа «расклад
 * изменился»: человек обязан увидеть, ЧТО стало другим, а не искать отличия глазами.
 */
export function changedRows(before: readonly PostingRow[], after: readonly PostingRow[]): Set<string> {
  const was = new Map(before.map(r => [rowKey(r), r.amount]));
  return new Set(after.filter(r => !was.has(rowKey(r)) || was.get(rowKey(r)) !== r.amount).map(rowKey));
}

export function rowKey(row: PostingRow): string {
  return `${row.kind}:${row.constructionId ?? ''}:${row.accountingOn}`;
}
