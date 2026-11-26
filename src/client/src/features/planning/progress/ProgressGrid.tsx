import { useLayoutEffect, useRef, useState } from 'react';
import type { ProgressColumn, ProgressRow, ProgressValue } from '@/shared/api/progress';
import { formatDate, formatQuantityUnit } from '@/shared/format/format';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Tooltip } from '@/shared/ui/Tooltip';
import { ProgressHidden } from './ProgressStrips';

const MIN_WIDTH_FOR_DETAILS = 800;
const rowKey = (row: ProgressRow) => `${row.workTypeId}/${row.unitId}`;
const workName = (row: ProgressRow) => row.workTypeName ?? 'Вид работы недоступен';

function datesText(value: ProgressValue | undefined) {
  if (value?.start && value.end) return `${formatDate(value.start)} — ${formatDate(value.end)}`;
  if (value?.start) return `С ${formatDate(value.start)}`;
  if (value?.end) return `До ${formatDate(value.end)}`;
  return null;
}

function cellText(column: ProgressColumn, row: ProgressRow) {
  if (column.state === 'hidden' || column.state === 'not-available') return null;
  const value = row.cells[column.code];
  return column.kind === 'Dates' ? datesText(value)
    : value?.volume == null ? null : formatQuantityUnit(value.volume, row.unitName, column.code === 'fact' ? 4 : 6);
}

function emptyText(column: ProgressColumn) {
  if (column.kind === 'Dates') return 'Срок отсутствует';
  return column.code === 'fact'
    ? column.state === 'partial' ? 'Нет включённого объёма' : 'Нет учтённого объёма'
    : 'Объём отсутствует';
}

function valueCell(column: ProgressColumn, row: ProgressRow, detail = false) {
  if (column.state === 'hidden') return <span className="text-on-surface-variant">
    <ProgressHidden permission={column.permission} />
  </span>;
  if (column.state === 'not-available') return <span className="text-on-surface-variant">
    {column.text || 'Данные недоступны'}
  </span>;
  const text = cellText(column, row);
  if (text !== null) return <span className="tabular-nums wrap-anywhere">{text}</span>;
  if (detail) return <span className="text-on-surface-variant">{emptyText(column)}</span>;
  return <Tooltip content={emptyText(column)}><span aria-label={emptyText(column)} className="text-on-surface-variant">—</span></Tooltip>;
}

function gridColumn(column: ProgressColumn, wide: boolean): DataGridColumn<ProgressRow> {
  return {
    key: column.code, label: column.title, align: 'right', width: wide ? 'w-36' : 'w-22',
    wrap: true,
    header: <span className="inline-flex flex-col whitespace-normal">
      {column.title}
      {column.state === 'partial' && <span className="text-label-small text-on-surface-variant">
        {wide ? 'Включённый объём' : 'Включено'}
      </span>}
    </span>,
    unavailable: column.state === 'hidden' ? 'no-right' : undefined,
    unavailableHint: column.permission ? `Скрыто правами · нужно ${column.permission}` : undefined,
    cell: row => valueCell(column, row),
  };
}

function ProgressRowDetails({ row, columns }: { row: ProgressRow; columns: ProgressColumn[] }) {
  return <aside aria-label="Подробности работы" className="min-w-0 rounded-md border border-outline-variant bg-surface p-4">
    <h3 className="text-title-medium wrap-anywhere" aria-live="polite">{workName(row)}</h3>
    <dl className="mt-2 divide-y divide-outline-variant text-body-medium">
      {columns.map(column => <div key={column.code} className="flex flex-wrap justify-between gap-x-3 gap-y-1 py-2">
        <dt className="text-on-surface-variant">
          {column.title}{column.state === 'partial' && <span className="block text-body-small">Включённый объём</span>}
        </dt>
        <dd className="min-w-0 wrap-anywhere">{valueCell(column, row, true)}</dd>
      </div>)}
    </dl>
  </aside>;
}

export function ProgressGrid({ columns, rows, section }: {
  columns: ProgressColumn[]; rows: ProgressRow[]; section: boolean;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [wide, setWide] = useState(false);
  const [selected, setSelected] = useState<string>();
  useLayoutEffect(() => {
    if (!host.current) return;
    const element = host.current;
    const update = () => {
      // Учитываем место внутри карточки после рейла и навигации стройки.
      setWide(element.getBoundingClientRect().width >= MIN_WIDTH_FOR_DETAILS);
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const row = rows.find(item => rowKey(item) === selected) ?? rows[0];
  const dates = columns.filter(column => column.kind === 'Dates');
  const gridColumns: DataGridColumn<ProgressRow>[] = [
    {
      key: 'name', label: 'Вид работы', rowHeader: true, wrap: true, width: wide ? undefined : 'w-40',
      cell: work => <>{workName(work)}{!wide && dates.map(column =>
        <span key={column.code} className="block text-body-small text-on-surface-variant">
          {column.title} · {valueCell(column, work)}
        </span>)}</>,
    },
    ...columns.filter(column => column.kind !== 'Dates').map(column => gridColumn(column, wide)),
  ];

  return <div ref={host} className={wide && row ? 'grid min-w-0 grid-cols-[minmax(0,2fr)_minmax(0,1fr)] items-start gap-4' : 'min-w-0'}>
    <DataGrid columns={gridColumns} rows={rows} rowKey={rowKey} fixed pinned={1}
      state={section ? 'filtered-out' : 'no-data'}
      onRowOpen={wide ? item => setSelected(rowKey(item)) : undefined}
      rowOpen={wide && row ? item => rowKey(item) === rowKey(row) : undefined} />
    {wide && row && <ProgressRowDetails row={row} columns={columns} />}
  </div>;
}
