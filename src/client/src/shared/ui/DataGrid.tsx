import { useLayoutEffect, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { Link } from 'react-router';
import { ArrowDown, ArrowUp, ArrowUpRight, EyeOff, Unlink, X } from 'lucide-react';
import { dtCard, dtTable, dtTh, dtTd, dtRow, dtNum } from './dataTable';
import {
  ABSENT_CELL_HINT, DATA_GRID_STATES, cellKind,
  type ColumnUnavailable, type GridState,
} from './dataGridStates';

/**
 * Общая сетка данных (задача G1a этапа 2, issue #1088, ТЗ CORE-33). Первый потребитель —
 * предпросмотр наборов данных; на ней же собран экран таблицы модуля (G1e, issue #1092).
 *
 * Чем она отличается от разметки `<table>` с классами `dataTable`: колонки ей ОБЪЯВЛЯЮТ, а не
 * выводятся из строк, и каждое «ничего не видно» она называет своим состоянием
 * (`dataGridStates`). Выводить колонки из первой строки — значит терять колонки, которых в ней
 * случайно нет, и путать отсутствующее поле с пустым значением.
 *
 * Сортировка, итоговая строка, закреплённые колонки и открытие строки — по желанию потребителя:
 * сетка их только рисует и сообщает о щелчке, а что по чему отсортировано и что открыто, знает
 * экран. Предпросмотру набора они не нужны, и без этих свойств сетка та же, что была.
 */

export interface DataGridColumn {
  key: string;
  label: string;
  /** Почему значений колонки не видно; null — видно. */
  unavailable?: ColumnUnavailable | null;
  align?: 'left' | 'right';
}

export interface DataGridSort {
  column: string;
  descending: boolean;
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
  sort, onSort, sortable, footer, pinned = 0, onRowOpen, rowOpen, rowLink, onRemoveColumn,
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
  /** По чему отсортировано — стрелкой в шапке; при нескольких ключах рядом стоит их порядок. */
  sort?: DataGridSort[];
  /** Щелчок по шапке. `additive` — с Shift: добавить колонку к сортировке, а не заменить её. */
  onSort?: (column: DataGridColumn, additive: boolean) => void;
  /** По каким колонкам сортируют; нет — по всем, у которых есть значения. */
  sortable?: (column: DataGridColumn) => boolean;
  /** Что стоит под колонкой в итоговой строке. Нет свойства — итоговой строки нет вовсе. */
  footer?: (column: DataGridColumn) => ReactNode;
  /** Сколько первых колонок закреплено слева: при прокрутке вбок они остаются на месте. */
  pinned?: number;
  /** Строку открывают — щелчком либо Enter / пробелом на ней. */
  onRowOpen?: (row: Row, index: number) => void;
  /** Какая строка сейчас открыта — она подсвечена. */
  rowOpen?: (row: Row, index: number) => boolean;
  /**
   * Куда ведёт строка: ссылка стоит в её первой клетке — та закрепляется первой и не уезжает вбок.
   * Настоящая ссылка, а не щелчок по строке: её открывают в новой вкладке и до неё доходят
   * клавишей Tab. null — этой строке вести некуда, и ссылки у неё нет.
   */
  rowLink?: (row: Row, index: number) => { to: string; label: string } | null;
  /**
   * Убрать колонку, которой в типе больше нет. Действие стоит в шапке такой колонки: она не
   * исчезает сама, но и держать её на экране человек не обязан.
   */
  onRemoveColumn?: (column: DataGridColumn) => void;
}) {
  const shown = state === 'module-off' ? [] : rows;
  const empty = shown.length === 0 ? DATA_GRID_STATES[state ?? 'no-data'] : null;
  const pinCount = Math.min(Math.max(pinned, 0), columns.length);
  const table = usePinOffsets(pinCount, columns.map(c => c.key).join('|'));

  // Закреплённая клетка липнет к левому краю и лежит НАД соседями: слой задан стилем, а не классом —
  // у шапки уже есть свой класс слоя, и два класса на одно свойство спорили бы порядком в CSS.
  const pin = (index: number) => (index < pinCount ? `sticky ${index === pinCount - 1 ? 'border-r' : ''}` : '');
  const pinStyle = (index: number, layer: number) => (index < pinCount
    ? { left: `var(--pin-${index}, 0px)`, zIndex: layer }
    : undefined);

  return (
    <div className={`${framed ? dtCard : 'overflow-auto'} ${className}`}>
      <table ref={table} className={dtTable}>
        <thead>
          <tr>
            {columns.map((c, i) => {
              const reason = c.unavailable ? DATA_GRID_STATES[c.unavailable] : null;
              const order = sort?.findIndex(s => s.column === c.key) ?? -1;
              const sorted = order >= 0 ? sort![order] : null;
              const canSort = onSort && (sortable ? sortable(c) : !c.unavailable);
              const label = (
                <span className={`inline-flex items-center gap-1 ${reason ? 'text-fg4' : ''}`}>
                  {c.unavailable && COLUMN_ICON[c.unavailable]}
                  <span className={c.unavailable === 'removed' ? 'line-through' : ''}>{c.label}</span>
                  {sorted && (sorted.descending ? <ArrowDown size={12} aria-hidden /> : <ArrowUp size={12} aria-hidden />)}
                  {sorted && sort!.length > 1 && <span className="text-[10px] text-fg4">{order + 1}</span>}
                </span>
              );
              return (
                <th key={c.key} data-pin={i < pinCount ? i : undefined}
                  className={`${dtTh} ${c.align === 'right' ? 'text-right' : ''} ${pin(i)}`}
                  style={pinStyle(i, 30)} title={reason?.hint}
                  aria-sort={!canSort ? undefined : !sorted ? 'none' : sorted.descending ? 'descending' : 'ascending'}>
                  {canSort ? (
                    <button type="button" onClick={e => onSort(c, e.shiftKey)}
                      title="Сортировать. С Shift — добавить колонку к сортировке"
                      className="hover:text-fg1 focus-visible:outline-2 rounded-sm">
                      {label}
                    </button>
                  ) : label}
                  {c.unavailable === 'removed' && onRemoveColumn && (
                    <button type="button" onClick={() => onRemoveColumn(c)}
                      aria-label={`Убрать колонку «${c.label}»`} title="Убрать колонку"
                      className="ml-1 align-middle text-fg4 hover:text-danger">
                      <X size={12} aria-hidden />
                    </button>
                  )}
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
          ) : shown.map((row, i) => {
            const open = rowOpen?.(row, i) ?? false;
            const link = rowLink?.(row, i) ?? null;
            return (
              <tr key={rowKey ? rowKey(row, i) : i}
                className={`${dtRow} ${onRowOpen ? 'cursor-pointer' : ''} ${open ? 'bg-brand-subtle' : ''}`}
                {...(onRowOpen ? {
                  tabIndex: 0,
                  'aria-selected': open,
                  onClick: () => { if (!selectingText()) onRowOpen(row, i); },
                  onKeyDown: (e: KeyboardEvent) => {
                    if (e.target !== e.currentTarget || (e.key !== 'Enter' && e.key !== ' ')) return;
                    e.preventDefault();
                    onRowOpen(row, i);
                  },
                } : {})}>
                {columns.map((c, ci) => (
                  <td key={c.key} style={pinStyle(ci, 5)}
                    className={`${dtTd} whitespace-nowrap text-fg1 ${c.align === 'right' ? dtNum : ''} `
                      + `${pin(ci)} ${ci < pinCount ? (open ? 'bg-brand-subtle' : 'bg-surface') : ''}`}>
                    {/* Щелчок по ссылке строку не открывает: это другое действие, и панель, мелькнувшая
                        перед уходом со страницы, осталась бы открытой по возвращении «назад». */}
                    {ci === 0 && link && (
                      <Link to={link.to} aria-label={link.label} title={link.label}
                        onClick={e => e.stopPropagation()}
                        className="mr-1.5 inline-flex align-middle rounded-sm text-fg3 hover:text-brand focus-visible:outline-2">
                        <ArrowUpRight size={14} aria-hidden />
                      </Link>
                    )}
                    <DataGridValue row={row} column={c}
                      renderValue={renderValue ? v => renderValue(v, c) : undefined} />
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
        {footer && (
          <tfoot>
            <tr>
              {columns.map((c, i) => (
                <td key={c.key} style={pinStyle(i, 30)}
                  className={`sticky bottom-0 z-10 bg-muted border-t border-stroke px-3 py-1.5 text-xs text-fg2 `
                    + `whitespace-nowrap align-top ${c.align === 'right' ? dtNum : ''} ${pin(i)}`}>
                  {footer(c)}
                </td>
              ))}
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  );
}

/** Щелчок, которым закончили выделять текст в строке, — не «открыть строку». */
function selectingText(): boolean {
  return (window.getSelection()?.toString() ?? '') !== '';
}

/**
 * Отступы закреплённых колонок. Липкой клетке нужен отступ слева — сумма ширин колонок перед ней, а
 * ширины знает только браузер. Меряем шапку и кладём отступы переменными на саму таблицу: клетки
 * читают их из CSS, и перерисовывать сетку на каждое изменение ширины не приходится.
 */
function usePinOffsets(pinned: number, columnKeys: string) {
  const table = useRef<HTMLTableElement>(null);

  useLayoutEffect(() => {
    const element = table.current;
    if (!element || pinned <= 0) return;

    const heads = [...element.querySelectorAll<HTMLElement>('th[data-pin]')];
    const measure = () => {
      let left = 0;
      heads.forEach((th, i) => {
        element.style.setProperty(`--pin-${i}`, `${left}px`);
        left += th.offsetWidth;
      });
    };
    measure();

    const observer = new ResizeObserver(measure);
    heads.forEach(th => observer.observe(th));
    return () => observer.disconnect();
  }, [pinned, columnKeys]);

  return table;
}
