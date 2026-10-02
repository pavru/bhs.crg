import { columnUnavailable, type GridState } from '@/shared/ui/dataGridStates';
import type { DataGridColumn } from '@/shared/ui/DataGrid';
import { formatDateRu } from '@/shared/utils/date';
import type { TableColumn, TableData } from '@/shared/api/tables';

/**
 * Таблица модуля → общая сетка (ТЗ CORE-33; задача G1d, issue #1091): колонки, клетки и состояние
 * «почему строк нет». Чистое — экран это только рисует.
 */

/** Колонки сетки из колонок таблицы: число — вправо, подпись смысла под отбором — в заголовке. */
export function gridColumns(columns: TableColumn[]): DataGridColumn[] {
  return columns.map(c => ({
    key: c.key,
    // «Сумма (доля: Комарова 36)» — что колонка значит под этим отбором, говорит заголовок.
    label: c.note ? `${c.label} (${c.note})` : c.label,
    unavailable: columnUnavailable(c.unavailable),
    align: c.kind === 'number' ? 'right' : 'left',
  }));
}

/** Клетка так, как её читает человек: дата — днём, флаг — «да / нет», перечень — через запятую. */
export function cellText(value: unknown, kind: string | undefined): string {
  if (Array.isArray(value)) return value.join(', ');
  if (typeof value === 'boolean') return value ? 'да' : 'нет';
  if (kind === 'date' && typeof value === 'string') return formatDateRu(value);
  if (kind === 'number' && typeof value === 'number')
    return value.toLocaleString('ru-RU', { maximumFractionDigits: 2 });
  return String(value);
}

/**
 * Почему строк нет. Различает экран, а не сетка: только он знает, что отбор был. Под отбором пустая
 * выдача — «отбор ничего не нашёл», а не «строк нет»: это разные ответы, и ведут они в разные
 * стороны — ослабить отбор или ждать данных.
 */
export function gridState(table: TableData | undefined, filtered: boolean): GridState {
  if (table?.state === 'module-off') return 'module-off';
  return filtered ? 'filtered-out' : 'no-data';
}

/** «Показано 200 из 1 340» — страница названа числом, а не обрезана молча. */
export function shownOf(table: TableData): string {
  const shown = table.rows.length;
  const total = table.count.toLocaleString('ru-RU');
  return shown < table.count ? `Показано ${shown.toLocaleString('ru-RU')} из ${total}` : `Строк: ${total}`;
}
