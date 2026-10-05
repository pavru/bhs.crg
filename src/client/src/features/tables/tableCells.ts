import { columnUnavailable, type GridState } from '@/shared/ui/dataGridStates';
import type { DataGridColumn } from '@/shared/ui/DataGrid';
import { formatCount, formatDay, formatNumber } from '@/shared/format/format';
import type { TableBreakdown, TableColumn, TableData, TableDeclaration } from '@/shared/api/tables';

/**
 * Таблица модуля → общая сетка (ТЗ CORE-33; задачи G1d и G1e, issue #1091, #1092): колонки, клетки,
 * состояние «почему строк нет» и слова над и под таблицей. Чистое — экран это только рисует.
 */

/**
 * Колонки сетки из колонок таблицы: число — вправо, подпись смысла под отбором — в заголовке.
 *
 * Колонка, закрытая правом, в сетку НЕ идёт: столбец одинаковых «скрыто правами» ничего не сообщает,
 * а ширину занимает. О ней говорит строка над таблицей (`hiddenByRight`) — числом, словами и кодом
 * права. Молча колонка не пропадает: сетка без неё и строка о ней стоят на экране вместе.
 */
export function gridColumns(columns: TableColumn[]): DataGridColumn[] {
  return columns.filter(c => c.unavailable !== 'no-right').map(c => ({
    key: c.key,
    // «Сумма (доля: Комарова 36)» — что колонка значит под этим отбором, говорит заголовок.
    label: c.note ? `${c.label} (${c.note})` : c.label,
    unavailable: columnUnavailable(c.unavailable),
    align: c.kind === 'number' ? 'right' : 'left',
  }));
}

/** Колонки, скрытые одним правом: сколько их, чего не хватает и какие именно. */
export interface HiddenColumns {
  count: number;
  /** Причина словами: «нет права на суммы». */
  reason: string;
  /** Код права — тот, по которому его ищет администратор. null — сервер кода не назвал. */
  requires: string | null;
  labels: string[];
}

/**
 * Колонки представления, закрытые правами, — по одной записи на право. `chosen` — колонки
 * представления; null — все колонки таблицы.
 *
 * Считаются только колонки, которые были бы ПОКАЗАНЫ: «3 колонки скрыты» над таблицей, из которой
 * человек сам убрал суммы, утверждало бы, что на экране чего-то не хватает.
 */
export function hiddenByRight(columns: TableColumn[], chosen: string[] | null): HiddenColumns[] {
  const groups = new Map<string, HiddenColumns>();
  for (const c of columns) {
    if (c.unavailable !== 'no-right' || (chosen && !chosen.includes(c.key))) continue;
    const reason = c.reason ?? 'нет права';
    const id = `${c.requires ?? ''}|${reason}`;
    const group = groups.get(id) ?? { count: 0, reason, requires: c.requires ?? null, labels: [] };
    group.count += 1;
    group.labels.push(c.label);
    groups.set(id, group);
  }
  return [...groups.values()];
}

/** «2 колонки скрыты» — начало строки над таблицей; причину и код права экран ставит следом. */
export function hiddenCountText(count: number): string {
  return `${count} ${plural(count, 'колонка скрыта', 'колонки скрыты', 'колонок скрыто')}`;
}

/** Клетка так, как её читает человек: дата — днём, флаг — «да / нет», перечень — через запятую. */
export function cellText(value: unknown, kind: string | undefined): string {
  if (Array.isArray(value)) return value.join(', ');
  if (typeof value === 'boolean') return value ? 'да' : 'нет';
  if (kind === 'date' && typeof value === 'string') return formatDay(value);
  if (kind === 'number' && typeof value === 'number') return formatNumber(value);
  return String(value);
}

/**
 * Почему строк нет. Различает экран, а не сетка: только он знает, что отбор был. Под отбором пустая
 * выдача — «отбор ничего не нашёл», а не «строк нет»: это разные ответы, и ведут они в разные
 * стороны — ослабить отбор или ждать данных.
 */
export function gridState(table: Pick<TableDeclaration, 'state'> | undefined, filtered: boolean): GridState {
  if (table?.state === 'module-off') return 'module-off';
  return filtered ? 'filtered-out' : 'no-data';
}

/**
 * «Строки 201–400 из 1 340» — страница названа числами, а не обрезана молча. Всё на одной странице
 * — «Строк: 17».
 */
export function shownOf(table: Pick<TableData, 'rows' | 'count' | 'offset'>): string {
  const shown = table.rows.length;
  const total = formatCount(table.count);
  if (shown >= table.count) return `Строк: ${total}`;
  // Страница за концом отбора (строки удалили, адрес остался) — строк на ней нет, и «1 341–1 340»
  // было бы бессмыслицей.
  if (shown === 0) return `Строк в отборе: ${total}, на этой странице их нет`;
  const from = formatCount(table.offset + 1);
  const to = formatCount(table.offset + shown);
  return `Строки ${from}–${to} из ${total}`;
}

/** Сколько страниц в отборе; пустой отбор — одна (пустая) страница. */
export function pageCount(count: number, size: number): number {
  return Math.max(1, Math.ceil(count / size));
}

/** Русское множественное: 1 колонка, 2 колонки, 5 колонок. */
export function plural(n: number, one: string, few: string, many: string): string {
  const tens = Math.abs(n) % 100;
  const units = tens % 10;
  if (tens > 10 && tens < 20) return many;
  if (units === 1) return one;
  return units >= 2 && units <= 4 ? few : many;
}

/** «Счёт целиком» — зерно таблицы с заглавной: что именно сложено, называет сама таблица. */
export function wholeLabel(grain: string): string {
  return grain ? `${grain[0].toUpperCase()}${grain.slice(1)} целиком` : 'Строка целиком';
}

/**
 * Суммы расшифровки так, как их читает человек. «В отборе» есть только под сужающим отбором — без
 * него строка и так целая, и вторая цифра читалась бы как «чего-то не хватает». Под сужающим она
 * есть ВСЕГДА, даже пустая: «отбор не назвал из этой строки ничего» — ответ, а не отсутствие ответа.
 */
export function breakdownTotals(
  breakdown: Pick<TableBreakdown, 'totals' | 'narrowed'> & Partial<Pick<TableBreakdown, 'columns'>>, grain: string,
): { key: string; label: string; value: string }[] {
  const money = (value: number | null) => (value === null ? '—' : cellText(value, 'number'));
  // Сумм несколько — каждая называет свою колонку: две пары «целиком / в отборе» иначе неразличимы.
  const of = (column: string) => (breakdown.totals.length > 1
    ? ` · ${breakdown.columns?.find(c => c.key === column)?.label ?? column}` : '');
  return breakdown.totals.flatMap(total => [
    { key: `${total.column}:whole`, label: wholeLabel(grain) + of(total.column), value: money(total.whole) },
    ...(breakdown.narrowed
      ? [{ key: `${total.column}:named`, label: `В отборе${of(total.column)}`, value: money(total.named) }] : []),
  ]);
}
