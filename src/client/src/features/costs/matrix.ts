import type {
  AllocationPartView, CostsConstruction, InvoiceView, LineAllocationView,
} from '@/shared/api/invoices';
import type { AllocationPreview, MatrixPart, MatrixState, SplitTarget } from '@/shared/api/allocationMatrix';
import { formatMoney } from './invoiceFields';
import { formatPlain } from './allocation';
import { toNumber } from './invoiceLines';

/**
 * Матрица разноски «строки × объекты» (F2, issue #1086, ТЗ COST-6.2, COST-12) — всё, что не рисование.
 *
 * <p><b>Клетка — это строка и объект</b> (стройка, возможно с разделом). Идентификатор части клетке не
 * нужен: у строки не бывает двух частей на одну цель (F1), и запись правит часть той же цели на месте.</p>
 *
 * <p>⚠️ <b>Числа клеток форма не считает.</b> Показанное до записи — ответ предпросмотра, после записи —
 * ответ счёта, и оба рисует {@link cellText} из одного и того же вида. Своя арифметика здесь только одна —
 * ОЦЕНКА остатка, пока человек набирает числа руками, и подписана она как оценка.</p>
 */

/** Строка счёта целиком — у счёта без строк (ТЗ COST-11). */
export const DOCUMENT_ROW = 'document';

export interface MatrixTarget {
  /** Ключ КОЛОНКИ — постоянный: смена стройки в заголовке не должна терять набранное в клетках. */
  key: string;
  construction: string;
  section: string | null;
  /** Процент для «по %» — текстом, как набран. */
  percent: string;
}

export interface MatrixRow {
  /** Идентификатор строки либо {@link DOCUMENT_ROW}. */
  key: string;
  lineId: string | null;
  title: string;
  mode: LineAllocationView['mode'];
  unit: string | null;
  /** Количество строки, её сумма (у строки без количества) или сумма к оплате (счёт целиком). */
  whole: number | null;
  amount: number | null;
}

/** Черновик клеток: строка → цель → число, как набрано. */
export type MatrixCells = Record<string, Record<string, string>>;

export function targetKey(construction: string, section: string | null): string {
  return `${construction}/${section ?? ''}`;
}

let columns = 0;

export function newTarget(construction = '', section: string | null = null): MatrixTarget {
  columns += 1;
  return { key: `колонка-${columns}`, construction, section, percent: '' };
}

/** Строки матрицы: строки счёта или, пока их нет, одна строка «счёт целиком». */
export function rowsOf(view: InvoiceView, total: number | null): MatrixRow[] {
  if (view.lines.length === 0)
    return [{
      key: DOCUMENT_ROW, lineId: null, title: 'Счёт целиком (строк нет)', mode: 'amount', unit: null,
      whole: total, amount: total,
    }];

  return view.lines.map(line => ({
    key: line.id,
    lineId: line.id,
    title: `${line.ordinal}. ${line.nomenclatureName ?? line.supplierText ?? 'без наименования'}`,
    mode: line.allocation.mode,
    unit: line.unit,
    whole: line.allocation.mode === 'quantity' ? line.quantity : line.amount,
    amount: line.amount,
  }));
}

/** Разноска каждой строки матрицы — из счёта или из предпросмотра, в одном и том же виде. */
export function allocationsOf(view: InvoiceView, preview: AllocationPreview | null): Record<string, LineAllocationView> {
  const document = preview?.summary.document ?? view.allocation.document;
  const lines = preview?.lines ?? Object.fromEntries(view.lines.map(l => [l.id, l.allocation]));

  return {
    ...lines,
    [DOCUMENT_ROW]: {
      mode: 'amount', parts: document.parts, unallocatedQuantity: null,
      unallocatedAmount: document.unallocatedAmount, balanced: document.balanced,
    },
  };
}

/**
 * Цели — объекты, на которые уже что-то разнесено, в порядке первого появления: по строкам, потом части
 * счёта целиком. Порядок устойчив, чтобы колонки не прыгали между чтениями.
 */
export function targetsOf(view: InvoiceView): MatrixTarget[] {
  const parts = [...view.lines.flatMap(l => l.allocation.parts), ...view.allocation.document.parts];
  const seen = new Map<string, MatrixTarget>();
  for (const part of parts) {
    const key = targetKey(part.constructionId, part.sectionId);
    if (!seen.has(key)) seen.set(key, newTarget(part.constructionId, part.sectionId));
  }
  return [...seen.values()];
}

export function partAt(
  allocation: LineAllocationView | undefined, target: { construction: string; section: string | null },
): AllocationPartView | undefined {
  return allocation?.parts.find(p => p.constructionId === target.construction && p.sectionId === target.section);
}

/**
 * Черновик клеток из вида. У суммы — БЕЗ расхождения со счётом: его добавил сервер при чтении, а
 * хранится часть без него (та же ловушка, что у диалога строки, F1).
 */
export function cellsOf(
  rows: MatrixRow[], targets: MatrixTarget[], allocations: Record<string, LineAllocationView>,
): MatrixCells {
  return Object.fromEntries(rows.map(row => [row.key, Object.fromEntries(targets.flatMap(target => {
    const part = partAt(allocations[row.key], target);
    if (!part) return [];
    return [[target.key, formatPlain(row.mode === 'quantity' ? part.quantity
      : part.amount === null ? null : Math.round((part.amount - part.discrepancy) * 100) / 100)]];
  }))]));
}

/** Черновик клеток из набора предпросмотра — по колонкам, чьи цели совпадают с целями частей. */
export function cellsFromState(rows: MatrixRow[], targets: MatrixTarget[], state: MatrixState): MatrixCells {
  const partsOf = (row: MatrixRow) => row.lineId === null
    ? state.document
    : state.lines.find(l => l.line === row.lineId)?.parts ?? [];

  return Object.fromEntries(rows.map(row => [row.key, Object.fromEntries(targets.flatMap(target => {
    const part = partsOf(row).find(p => p.construction === target.construction && p.section === target.section);
    return part ? [[target.key, formatPlain(part.quantity ?? part.amount)]] : [];
  }))]));
}

/**
 * Набор для записи. Пустая клетка и ноль — «части нет»: ноль сервер отвергает как «не доля».
 *
 * ⚠️ Набранное, но не число («12,5,», «abc»), уезжает как ПУСТОЕ значение, а не выбрасывается: отказ
 * сервера назовёт часть, а молча выброшенная клетка исчезла бы вместе с тем, что человек в неё вписал.
 */
export function toState(rows: MatrixRow[], targets: MatrixTarget[], cells: MatrixCells): MatrixState {
  const partsOf = (row: MatrixRow): MatrixPart[] => targets
    .map(target => ({ target, text: (cells[row.key]?.[target.key] ?? '').trim() }))
    .filter(({ text }) => text !== '' && toNumber(text) !== 0)
    .map(({ target, text }) => ({ target, value: toNumber(text) }))
    .map(({ target, value }) => ({
      construction: target.construction,
      section: target.section,
      quantity: row.mode === 'quantity' ? value : null,
      amount: row.mode === 'quantity' ? null : value,
    }));

  const document = rows.find(r => r.key === DOCUMENT_ROW);
  return {
    lines: rows.filter(r => r.lineId !== null).map(row => ({ line: row.lineId!, parts: partsOf(row) })),
    document: document ? partsOf(document) : [],
  };
}

/** Цели для быстрой разноски. Процент уезжает только у «по %». */
export function toSplitTargets(targets: MatrixTarget[], withPercent: boolean): SplitTarget[] {
  return targets.filter(t => t.construction).map(t => ({
    construction: t.construction,
    section: t.section,
    percent: withPercent ? toNumber(t.percent) : null,
  }));
}

/** ОЦЕНКА остатка строки по набранному — пока клетки не записаны. */
export function estimateRest(row: MatrixRow, targets: MatrixTarget[], cells: MatrixCells): number | null {
  if (row.whole === null) return null;
  const given = targets.reduce((sum, t) => sum + (toNumber(cells[row.key]?.[t.key] ?? '') ?? 0), 0);
  const digits = row.mode === 'quantity' ? 1000 : 100;
  return Math.round((row.whole - given) * digits) / digits;
}

/**
 * Текст клетки — ОДНА функция для предпросмотра и для записанного, поэтому они совпадают посимвольно.
 * ⚠️ Ноль пишется «0», а не остаётся пустым: пустая клетка не отличима от недогруженной.
 */
export function cellText(row: MatrixRow, part: AllocationPartView | undefined): string {
  if (!part) return '0';
  if (row.mode === 'quantity')
    return `${formatPlain(part.quantity)}${row.unit ? ` ${row.unit}` : ''}`
      + (part.amount === null ? '' : ` · ${formatMoney(part.amount)}`);
  return part.amount === null ? '0' : formatMoney(part.amount);
}

/** «Не разнесено» строки — тоже одной функцией и тоже с нулём. */
export function restText(row: MatrixRow, allocation: LineAllocationView | undefined): string {
  if (!allocation) return '0';
  if (row.mode === 'quantity')
    return `${formatPlain(allocation.unallocatedQuantity ?? 0)}${row.unit ? ` ${row.unit}` : ''}`;
  return allocation.unallocatedAmount === null ? '—' : formatMoney(allocation.unallocatedAmount);
}

/** Название цели для заголовка колонки и для шапки счёта. */
export function targetName(target: { construction: string; section: string | null }, sites: CostsConstruction[] | undefined): string {
  const site = sites?.find(s => s.id === target.construction);
  if (!site) return target.construction ? 'стройка удалена' : 'объект не выбран';
  if (!target.section) return site.name;
  return `${site.name} / ${site.sections.find(s => s.id === target.section)?.name ?? 'раздел удалён'}`;
}

/**
 * «Объект» в шапке счёта (ТЗ COST-6.2) — выведен из разноски, а не хранится отдельно: храни его полем,
 * у одного вопроса «на что разнесён счёт» было бы два ответа, и расходились бы они на первой правке
 * матрицы.
 */
export function headerObject(view: InvoiceView): { kind: 'none' } | { kind: 'one'; construction: string; complete: boolean } | { kind: 'many'; count: number } {
  // Колонки матрицы здесь не заводятся: шапка рисуется часто, а ей нужны только стройки.
  const constructions = [...new Set([...view.lines.flatMap(l => l.allocation.parts), ...view.allocation.document.parts]
    .map(p => p.constructionId))];
  if (constructions.length === 0) return { kind: 'none' };
  if (constructions.length > 1) return { kind: 'many', count: constructions.length };
  return { kind: 'one', construction: constructions[0], complete: view.allocation.allocated };
}
