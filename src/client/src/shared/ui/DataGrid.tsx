import type { ReactNode } from 'react';
import { EyeOff, Unlink } from 'lucide-react';
import { dtCard, dtTable, dtTh, dtTd, dtRow, dtNum } from './dataTable';
import {
  ABSENT_CELL_HINT, DATA_GRID_STATES, cellKind,
  type ColumnUnavailable, type GridState,
} from './dataGridStates';

/**
 * Общая сетка данных (задача G1a этапа 2, issue #1088, ТЗ CORE-33). Первый потребитель —
 * предпросмотр наборов данных; дальше на ней собираются табличные экраны модулей (G1b–G1e).
 *
 * Чем она отличается от разметки `<table>` с классами `dataTable`: колонки ей ОБЪЯВЛЯЮТ, а не
 * выводятся из строк, и каждое «ничего не видно» она называет своим состоянием
 * (`dataGridStates`). Выводить колонки из первой строки — значит терять колонки, которых в ней
 * случайно нет, и путать отсутствующее поле с пустым значением.
 */

export interface DataGridColumn {
  key: string;
  label: string;
  /** Почему значений колонки не видно; null — видно. */
  unavailable?: ColumnUnavailable | null;
  align?: 'left' | 'right';
}

const COLUMN_ICON: Record<ColumnUnavailable, ReactNode> = {
  'no-right': <EyeOff size={12} aria-hidden />,
  removed: <Unlink size={12} aria-hidden />,
};

/**
 * Значение одной клетки — с теми же различиями, что в сетке: колонка без значений, поля нет в
 * строке, значение пустое. Вынесено, чтобы и вертикальная раскладка (одна запись «поле — значение»)
 * говорила то же самое, а не печатала пустоту по-своему.
 */
export function DataGridValue({ row, column, renderValue }: {
  row: Record<string, unknown>;
  column: DataGridColumn;
  renderValue?: (value: unknown) => ReactNode;
}) {
  const kind = cellKind(row, column.key);
  if (column.unavailable) {
    const state = DATA_GRID_STATES[column.unavailable];
    // Значение у такой колонки бывает (строки таблицы генерация пишет и вне схемы) — тогда оно
    // видно, но приглушено и с причиной. Нет значения — клетка называет причину, а не пустует.
    if (kind === 'value')
      return <span className="text-fg4" title={state.hint}>{renderValue ? renderValue(row[column.key]) : String(row[column.key])}</span>;
    return <span className="text-fg4 italic" title={state.hint}>{state.title}</span>;
  }
  switch (kind) {
    case 'absent':
      return <span className="text-fg4" title={ABSENT_CELL_HINT} aria-label={ABSENT_CELL_HINT}>—</span>;
    // Пустое значение — пустая клетка: так его читает любой, кто видел таблицу. Слово «null» на
    // экране было отладочным выводом, а не ответом.
    case 'empty':
      return null;
    default:
      return <>{renderValue ? renderValue(row[column.key]) : String(row[column.key])}</>;
  }
}

export function DataGrid<Row extends Record<string, unknown>>({
  columns, rows, state, renderValue, rowKey, framed = true, className = '',
}: {
  columns: DataGridColumn[];
  rows: Row[];
  /**
   * Почему строк нет. `module-off` перекрывает строки целиком; остальные показываются, когда строк
   * нет, — по умолчанию `no-data`. Передавать `filtered-out` — дело экрана: только он знает, что
   * отбор был.
   */
  state?: GridState;
  renderValue?: (value: unknown, column: DataGridColumn) => ReactNode;
  rowKey?: (row: Row, index: number) => string;
  /** Своя рамка-карточка; false — сетка внутри чужой карточки (иначе рамка в рамке). */
  framed?: boolean;
  className?: string;
}) {
  const shown = state === 'module-off' ? [] : rows;
  const empty = shown.length === 0 ? DATA_GRID_STATES[state ?? 'no-data'] : null;

  return (
    <div className={`${framed ? dtCard : 'overflow-auto'} ${className}`}>
      <table className={dtTable}>
        <thead>
          <tr>
            {columns.map(c => {
              const reason = c.unavailable ? DATA_GRID_STATES[c.unavailable] : null;
              return (
                <th key={c.key} className={`${dtTh} ${c.align === 'right' ? 'text-right' : ''}`}
                  title={reason?.hint}>
                  <span className={`inline-flex items-center gap-1 ${reason ? 'text-fg4' : ''}`}>
                    {c.unavailable && COLUMN_ICON[c.unavailable]}
                    <span className={c.unavailable === 'removed' ? 'line-through' : ''}>{c.label}</span>
                  </span>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {empty ? (
            <tr>
              <td colSpan={Math.max(columns.length, 1)} className={`${dtTd} text-center py-4`}>
                <div className="text-sm text-fg2">{empty.title}</div>
                <div className="text-xs text-fg4">{empty.hint}</div>
              </td>
            </tr>
          ) : shown.map((row, i) => (
            <tr key={rowKey ? rowKey(row, i) : i} className={dtRow}>
              {columns.map(c => (
                <td key={c.key}
                  className={`${dtTd} whitespace-nowrap text-fg1 ${c.align === 'right' ? dtNum : ''}`}>
                  <DataGridValue row={row} column={c}
                    renderValue={renderValue ? v => renderValue(v, c) : undefined} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
