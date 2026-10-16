import type { TableBreakdown } from '@/shared/api/tables';
import { DataGrid, DataGridValue, type DataGridColumn } from '@/shared/ui/DataGrid';
import { columnUnavailable } from '@/shared/ui/dataGridStates';
import { breakdownTotals, cellText } from './tableCells';

type BreakdownRow = TableBreakdown['rows'][number];

/**
 * Колонки расшифровки. Закрытая колонка называет причину словами сетки — теми же, что у клетки
 * строки рядом, в той же панели. Названное отбором помечено и словом — своей колонкой.
 */
function breakdownColumns(breakdown: TableBreakdown): DataGridColumn<BreakdownRow>[] {
  const columns: DataGridColumn<BreakdownRow>[] = breakdown.columns.map(c => {
    const column: DataGridColumn<BreakdownRow> = {
      key: c.key, label: c.label, unavailable: columnUnavailable(c.unavailable),
      align: c.kind === 'number' ? 'right' : 'left', wrap: c.kind !== 'number',
    };
    column.cell = row => <DataGridValue row={row.values} column={column} renderValue={v => cellText(v, c.kind)} />;
    return column;
  });
  if (breakdown.narrowed)
    columns.push({ key: 'named', label: 'Отбор', cell: row => row.named ? <span className="text-brand">в отборе</span> : null });
  return columns;
}

/**
 * Расшифровка строки в боковой панели: счёт по объектам и учётным месяцам (G4, issue #1097).
 *
 * Блок показывает строку ЦЕЛИКОМ при любом отборе — иначе по нему не понять, какая часть счёта дала
 * клетку «Сумма». Названное отбором помечено СЛОВОМ «в отборе», а не только начертанием: цвет и
 * жирность читалка экрана не произносит. Про счета блок не знает — заголовок, колонки и суммы
 * приходят с сервера.
 */
export function RowBreakdown({ breakdown, grain }: { breakdown: TableBreakdown; grain: string }) {
  const totals = breakdownTotals(breakdown, grain);

  // Блок не сошёлся со строкой — на его месте отказ, а не пустая разноска: «строк нет» читалось бы
  // как «счёт не разнесён».
  if (breakdown.refusal)
    return (
      <section aria-label={breakdown.title} className="mb-3 pb-3 border-b border-stroke">
        <h3 className="text-xs font-medium uppercase tracking-wide text-fg3">{breakdown.title}</h3>
        <p role="alert" className="mt-0.5 text-sm text-danger">{breakdown.refusal}</p>
      </section>
    );

  return (
    <section aria-label={breakdown.title} className="mb-3 pb-3 border-b border-stroke">
      <h3 className="text-xs font-medium uppercase tracking-wide text-fg3">{breakdown.title}</h3>
      {breakdown.note && <p className="mt-0.5 text-xs text-fg3">{breakdown.note}</p>}

      <DataGrid density="compact" framed={false} className="mt-1.5" columns={breakdownColumns(breakdown)}
        rows={breakdown.rows} emphasized={row => row.named} />

      {totals.length > 0 && (
        <dl className="mt-1.5 pt-1.5 border-t border-stroke/60 text-sm">
          {totals.map(total => (
            <div key={total.key} className="flex justify-between gap-3">
              <dt className="text-fg3">{total.label}</dt>
              <dd className="tabular-nums text-fg1">{total.value}</dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}
