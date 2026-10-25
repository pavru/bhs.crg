import type { AllocationPartView, InvoiceView, LineAllocationView, VatMode } from '@/shared/api/invoices';
import type { AllocationPreview, MatrixPart, MatrixState, SplitTarget } from '@/shared/api/allocationMatrix';
import { formatInput, formatMoney, formatQuantity } from '@/shared/format/format';
import { K } from './invoiceFields';
import { preview as linePreview, toNumber, type LineDraft } from './invoiceLines';
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

const documentRow = (total: number | null): MatrixRow => ({
  key: DOCUMENT_ROW, lineId: null, title: 'Счёт целиком (строк нет)', mode: 'amount', unit: null,
  whole: total, amount: total,
});

/** Строки матрицы: строки счёта или, пока их нет, одна строка «счёт целиком». */
export function rowsOf(view: InvoiceView, total: number | null): MatrixRow[] {
  if (view.lines.length === 0) return [documentRow(total)];

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

/**
 * Строки матрицы по ЧЕРНОВИКУ строк — у исправления (E2, issue #1100, PR 6b-1): строки правят тем же
 * телом, что и разноску, и новую строку надо разнести, не записывая её отдельно. Строка зовётся ключом
 * черновика (у записанной это её идентификатор), вид разноски — тем же правилом, что у сервера
 * (`AllocationMath.ModeOf`): есть количество — делится количество, нет — сумма.
 */
export function draftRows(drafts: readonly LineDraft[], total: number | null, vat: VatMode): MatrixRow[] {
  if (drafts.length === 0) return [documentRow(total)];
  return drafts.map((draft, index) => {
    const quantity = toNumber(draft.quantity);
    // Делятся деньги с НДС (issue #1499): сверху это не бумажная сумма строки, а она плюс НДС.
    const amount = linePreview(draft, vat).gross;
    const mode = quantity !== null && quantity > 0 ? 'quantity' : amount !== null && amount !== 0 ? 'amount' : 'none';
    return {
      key: draft.key, lineId: draft.key,
      title: `${index + 1}. ${draft.nomenclatureName ?? (draft.supplierText.trim() || 'без наименования')}`,
      mode, unit: draft.unit.trim() || null, whole: mode === 'quantity' ? quantity : amount, amount,
    };
  });
}

/**
 * Из чего собрана матрица (issue #1176): отметка разноски, сумма к оплате (ею разносится счёт без
 * строк) и то из строк, что делится, — количество, сумма, единица.
 *
 * ⚠️ Не строки целиком: название позиции приезжает в том же виде, а меняется в справочнике, без счёта.
 * Попади оно в подпись, переименованная позиция читалась бы как «счёт изменили» (ревью PR #1208).
 */
export function matrixSignature(view: InvoiceView): string {
  return JSON.stringify([
    view.allocation.stamp, view.requisites[K.total] ?? null,
    view.lines.map(line => [line.id, line.ordinal, line.unit, line.quantity, line.amount]),
  ]);
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

/** Цели набора, уже лежащего в черновике исправления, — тем же порядком первого появления. */
export function targetsOfState(state: MatrixState): MatrixTarget[] {
  const seen = new Map<string, MatrixTarget>();
  for (const part of [...state.lines.flatMap(l => l.parts), ...state.document]) {
    const place = { construction: part.construction, section: part.section, article: part.article };
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

/** Раздел, на котором лежит весь счёт: id, `null` — стройка без раздела, `'many'` — несколько мест стройки. */
export type HeaderSection = string | null | 'many';

export type HeaderObject =
  | { kind: 'none' }
  /**
   * Все части — на ОДНОЙ стройке (или одной статье). `place` — стройка без раздела либо статья; раздел —
   * отдельно, в `section`. `sections` — сколько мест этой стройки занято («без раздела» — тоже место).
   */
  | { kind: 'one'; place: Place; section: HeaderSection; sections: number; complete: boolean }
  | { kind: 'many'; count: number };

/**
 * «Объект» в шапке счёта (ТЗ COST-6.2, COST-12.1) — выведен из разноски, а не хранится отдельно: храни его
 * полем, у одного вопроса «на что разнесён счёт» было бы два ответа, и расходились бы они на первой правке
 * матрицы.
 *
 * <p>Объект — стройка ЦЕЛИКОМ или статья вне строек; разделы одной стройки — всё ещё один объект, но шапка
 * их различает (D2): весь счёт на одном разделе, без раздела или на нескольких местах стройки. Потерянный
 * раздел (удалён, у другой стройки) — такой же `id`: его называет `placeName`.</p>
 */
export function headerObject(view: InvoiceView): HeaderObject {
  // Колонки матрицы здесь не заводятся: шапка рисуется часто.
  const objects = new Map<string, { place: Place; sections: Set<string | null> }>();
  for (const part of [...view.lines.flatMap(l => l.allocation.parts), ...view.allocation.document.parts]) {
    const { section, ...rest } = placeOfPart(part);
    const place = { ...rest, section: null };
    const object = objects.get(placeKey(place)) ?? { place, sections: new Set<string | null>() };
    object.sections.add(section);
    objects.set(placeKey(place), object);
  }
  if (objects.size === 0) return { kind: 'none' };
  if (objects.size > 1) return { kind: 'many', count: objects.size };

  const [{ place, sections }] = objects.values();
  const section = sections.size > 1 ? 'many' : [...sections][0];
  return { kind: 'one', place, section, sections: sections.size, complete: view.allocation.allocated };
}

/** Место, на котором лежит весь счёт, — стройка с разделом; у нескольких мест стройки — `null`. */
export function headerPlace(current: HeaderObject): Place | null {
  if (current.kind !== 'one' || current.section === 'many') return null;
  return { ...current.place, section: current.section };
}

/**
 * Выбор в шапке пишется сразу или открывает матрицу (D3, решение владельца 24.10.2026). Сразу — когда на
 * стройке нет чужой раскладки по местам, которую выбор потерял бы: счёт не разнесён, или весь лежит на одном
 * месте той же стройки (неполная разноска — тоже: остаток ложится туда же). Несколько мест, другая стройка
 * или статья — матрица с предпросмотром поверх прежней разноски: заменять чужое решение молча нельзя.
 */
export function headerPickMode(current: HeaderObject, picked: Place): 'write' | 'matrix' {
  if (current.kind === 'none') return 'write';
  const at = headerPlace(current);
  if (at === null) return 'matrix';
  if (samePlace(at, picked)) return 'write';
  return at.construction !== null && at.construction === picked.construction ? 'write' : 'matrix';
}
