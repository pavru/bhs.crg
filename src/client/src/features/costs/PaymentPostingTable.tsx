import { useState } from 'react';
import { ArrowRightLeft, ChevronDown, ChevronRight } from 'lucide-react';
import type { PaymentPosting, PostingRow } from '@/shared/api/invoicePayment';
import { formatDate, formatMoney } from './invoiceFields';
import { CALM_ROWS_SHOWN, foldedLabel, movedWarning, rowKey, summarize } from './paymentPosting';

/**
 * Расклад оплаты: какой контур каким днём входит в затраты (задача C5, issue #1082).
 *
 * <p><b>Строка — контур, а не доля.</b> Учётную дату определяет стройка, а не строка счёта: двадцать
 * строк на три стройки — это три строки расклада; до долей строка раскрывается.</p>
 *
 * <p><b>Переносимые не сворачиваются никогда</b> — сворачиваются только те, чья учётная дата и есть
 * дата платежа: о них человеку решать нечего.</p>
 */
export function PaymentPostingTable({ posting, changed, expanded = false }: {
  posting: PaymentPosting;
  /** Строки, изменившиеся после отказа «расклад изменился», — подсвечиваются. */
  changed?: ReadonlySet<string>;
  /** Показать таблицу сразу, даже когда переносов нет (чтение записанного расклада). */
  expanded?: boolean;
}) {
  const summary = summarize(posting.rows);
  const [shown, setShown] = useState(expanded);
  const [unfolded, setUnfolded] = useState(false);
  const warning = movedWarning(summary, posting.total);

  const calm = unfolded ? summary.calm : summary.calm.slice(0, CALM_ROWS_SHOWN);
  const folded = unfolded ? [] : summary.calm.slice(CALM_ROWS_SHOWN);

  if (!warning && !shown)
    return (
      <div className="text-[13px] text-fg2">
        Вся сумма войдёт в затраты {formatDate(posting.paidOn)}.{' '}
        <button type="button" className="underline decoration-dotted underline-offset-2 text-fg3"
          onClick={() => setShown(true)}>
          Показать расклад
        </button>
      </div>
    );

  return (
    <div className="space-y-2">
      {warning && (
        <p role="note" className="flex items-start gap-1.5 text-[13px] text-warning">
          <ArrowRightLeft size={14} className="shrink-0 mt-0.5" /> {warning}
        </p>
      )}
      <div className="max-h-72 overflow-y-auto rounded-lg border border-stroke">
        <table className="w-full text-xs">
          <thead className="bg-base text-fg3 sticky top-0">
            <tr className="text-left">
              <th className="px-3 py-1.5 font-medium">Контур</th>
              <th className="px-3 py-1.5 font-medium text-right w-36">Сумма</th>
              <th className="px-3 py-1.5 font-medium w-28">Учётная дата</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-muted">
            {[...summary.moved, ...calm].map(row => (
              <Row key={rowKey(row)} row={row} highlighted={changed?.has(rowKey(row)) === true} />
            ))}
            {folded.length > 0 && (
              <tr>
                <td colSpan={3} className="px-3 py-1.5">
                  <button type="button" className="underline decoration-dotted underline-offset-2 text-fg3"
                    onClick={() => setUnfolded(true)}>
                    {foldedLabel(folded)}
                  </button>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Row({ row, highlighted }: { row: PostingRow; highlighted: boolean }) {
  const [open, setOpen] = useState(false);
  const expandable = row.parts.length > 0;

  return (
    <>
      <tr className={`align-top ${highlighted ? 'bg-warning-subtle' : ''}`}>
        <td className="px-3 py-1.5">
          <button type="button" disabled={!expandable} onClick={() => setOpen(o => !o)}
            aria-expanded={expandable ? open : undefined}
            className="inline-flex items-center gap-1 text-left text-fg1 disabled:cursor-default">
            {expandable && (open ? <ChevronDown size={12} /> : <ChevronRight size={12} />)}
            {row.name}
          </button>
          {row.note && <div className="text-warning mt-0.5">{row.note}</div>}
        </td>
        <td className="px-3 py-1.5 text-right tabular-nums text-fg1">
          {row.amount === null ? '—' : formatMoney(row.amount)}
        </td>
        <td className={`px-3 py-1.5 tabular-nums ${row.moved ? 'text-warning font-medium' : 'text-fg2'}`}>
          {formatDate(row.accountingOn)}
        </td>
      </tr>
      {open && row.parts.map((part, index) => (
        <tr key={index} className="text-fg3">
          <td className="pl-8 pr-3 py-1">{part.label}</td>
          <td className="px-3 py-1 text-right tabular-nums">{part.amount === null ? '—' : formatMoney(part.amount)}</td>
          <td />
        </tr>
      ))}
    </>
  );
}
