import type { AllocationPartView, InvoiceView, LineAllocationView } from '@/shared/api/invoices';
import type { AllocationPreview, MatrixPart, MatrixState, SplitTarget } from '@/shared/api/allocationMatrix';
import { formatInput, formatMoney, formatQuantity } from '@/shared/format/format';
import { toNumber } from './invoiceLines';
import { NO_PLACE, chosen, placeKey, placeName, placeOfPart, samePlace, type Place, type Places } from './places';

/**
 * Матрица разноски «строки × объекты» (F2, issue #1086, ТЗ COST-6.2, COST-12) — всё, что не рисование.
 *
 * <p><b>Клетка — это строка и объект</b> (стройка, возможно с разделом, или статья вне строек — F3). Идентификатор части клетке не
 * нужен: у строки не бывает двух частей на одну цель (F1), и запись правит часть той же цели на месте.</p>
 *
 * <p>⚠️ <b>Числа клеток форма не считает.</b> Показанное до записи — ответ предпросмотра, после записи —
 * ответ счёта, и оба рисует {@link cellText} из одного и того же вида. Своя арифметика здесь только одна —
 * ОЦЕНКА остатка, пока человек набирает числа руками, и подписана она как оценка.</p>
 */

/** Строка счёта целиком — у счёта без строк (ТЗ COST-11). */
export const DOCUMENT_ROW = 'document';

export interface MatrixTarget extends Place {
  /** Ключ КОЛОНКИ — постоянный: смена стройки в заголовке не должна терять набранное в клетках. */
  key: string;
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

let columns = 0;

export function newTarget(place: Place = NO_PLACE): MatrixTarget {
  columns += 1;
  return { key: `колонка-${columns}`, ...place, percent: '' };
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
    const place = placeOfPart(part);
    if (!seen.has(placeKey(place))) seen.set(placeKey(place), newTarget(place));
  }
  return [...seen.values()];
}

export function partAt(allocation: LineAllocationView | undefined, target: Place): AllocationPartView | undefined {
  return allocation?.parts.find(p => samePlace(placeOfPart(p), target));
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
    return [[target.key, formatInput(row.mode === 'quantity' ? part.quantity
      : part.amount === null ? null : Math.round((part.amount - part.discrepancy) * 100) / 100, 3)]];
  }))]));
}

/** Черновик клеток из набора предпросмотра — по колонкам, чьи цели совпадают с целями частей. */
export function cellsFromState(rows: MatrixRow[], targets: MatrixTarget[], state: MatrixState): MatrixCells {
  const partsOf = (row: MatrixRow) => row.lineId === null
    ? state.document
    : state.lines.find(l => l.line === row.lineId)?.parts ?? [];

  return Object.fromEntries(rows.map(row => [row.key, Object.fromEntries(targets.flatMap(target => {
    const part = partsOf(row).find(p => samePlace(p, target));
    return part ? [[target.key, formatInput(part.quantity ?? part.amount, 3)]] : [];
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
      article: target.article,
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
  return targets.filter(chosen).map(t => ({
    construction: t.construction,
    section: t.section,
    article: t.article,
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
    return `${part.quantity === null ? '' : formatQuantity(part.quantity)}${row.unit ? ` ${row.unit}` : ''}`
      + (part.amount === null ? '' : ` · ${formatMoney(part.amount)}`);
  return part.amount === null ? '0' : formatMoney(part.amount);
}

/** «Не разнесено» строки — тоже одной функцией и тоже с нулём. */
export function restText(row: MatrixRow, allocation: LineAllocationView | undefined): string {
  if (!allocation) return '0';
  if (row.mode === 'quantity')
    return `${formatQuantity(allocation.unallocatedQuantity ?? 0)}${row.unit ? ` ${row.unit}` : ''}`;
  return allocation.unallocatedAmount === null ? '—' : formatMoney(allocation.unallocatedAmount);
}

/** Название цели для заголовка колонки и для шапки счёта. */
export function targetName(target: Place, places: Places): string {
  return placeName(target, places);
}

/**
 * «Объект» в шапке счёта (ТЗ COST-6.2) — выведен из разноски, а не хранится отдельно: храни его полем,
 * у одного вопроса «на что разнесён счёт» было бы два ответа, и расходились бы они на первой правке
 * матрицы.
 */
export function headerObject(
  view: InvoiceView,
): { kind: 'none' } | { kind: 'one'; place: Place; complete: boolean } | { kind: 'many'; count: number } {
  // Колонки матрицы здесь не заводятся: шапка рисуется часто. Объект — стройка ЦЕЛИКОМ (разделы одной
  // стройки — всё ещё один объект) или статья вне строек.
  const objects = new Map<string, Place>();
  for (const part of [...view.lines.flatMap(l => l.allocation.parts), ...view.allocation.document.parts]) {
    const place = { ...placeOfPart(part), section: null };
    objects.set(placeKey(place), place);
  }
  if (objects.size === 0) return { kind: 'none' };
  if (objects.size > 1) return { kind: 'many', count: objects.size };
  return { kind: 'one', place: [...objects.values()][0], complete: view.allocation.allocated };
}
