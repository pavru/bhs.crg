import type { TableBreakdown } from '@/shared/api/tables';
import { breakdownTotals, cellText } from './tableCells';

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

      <table className="mt-1.5 w-full text-sm">
        <thead>
          <tr>
            {breakdown.columns.map(c => (
              <th key={c.key} scope="col"
                className={`py-0.5 pr-2 text-xs font-normal text-fg4 ${c.kind === 'number' ? 'text-right' : 'text-left'}`}>
                {c.label}
              </th>
            ))}
            {breakdown.narrowed && <th scope="col"><span className="sr-only">Названо отбором</span></th>}
          </tr>
        </thead>
        <tbody>
          {breakdown.rows.length === 0 && (
            <tr><td colSpan={breakdown.columns.length} className="py-0.5 text-fg3">Строк нет.</td></tr>
          )}
          {breakdown.rows.map((row, i) => (
            <tr key={i} className={row.named ? 'font-medium text-fg1' : 'text-fg2'}>
              {breakdown.columns.map(c => (
                <td key={c.key}
                  className={`py-0.5 pr-2 align-top ${c.kind === 'number' ? 'text-right tabular-nums whitespace-nowrap' : 'break-words'}`}>
                  {c.unavailable
                    // Закрытая колонка называет причину, а не молчит: пустая клетка читалась бы как ноль.
                    ? <span className="text-xs text-fg4">{c.reason ?? 'недоступно'}</span>
                    : row.values[c.key] == null ? <span className="text-fg4">—</span> : cellText(row.values[c.key], c.kind)}
                </td>
              ))}
              {breakdown.narrowed && (
                <td className="py-0.5 align-top text-xs text-brand whitespace-nowrap">{row.named ? 'в отборе' : ''}</td>
              )}
            </tr>
          ))}
        </tbody>
      </table>

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
