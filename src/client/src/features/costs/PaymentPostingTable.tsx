import { useState } from 'react';
import { ArrowRightLeft, ChevronDown, ChevronRight } from 'lucide-react';
import type { PaymentPosting, PostingPart, PostingRow } from '@/shared/api/invoicePayment';
import { formatDate, formatMoney } from '@/shared/format/format';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { toggleInSet } from '@/shared/utils/toggleInSet';
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
  // Раскрытые до долей контуры — по ключу строки: расклад перечитывается после каждой правки.
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  const warning = movedWarning(summary, posting.total);

  const calm = unfolded ? summary.calm : summary.calm.slice(0, CALM_ROWS_SHOWN);
  const folded = unfolded ? [] : summary.calm.slice(CALM_ROWS_SHOWN);

  if (!warning && !shown)
    return (
      <div className="text-note text-on-surface-variant">
        Вся сумма войдёт в затраты {formatDate(posting.paidOn)}.{' '}
        <button type="button" className="underline decoration-dotted underline-offset-2 text-fg3"
          onClick={() => setShown(true)}>
          Показать расклад
        </button>
      </div>
    );

  const columns: DataGridColumn<Line>[] = [
    { key: 'name', label: 'Контур', wrap: true,
      cell: ({ key, row, part }) => {
        if (part) return <span className="pl-5 text-fg3">{part.label}</span>;
        const expandable = row.parts.length > 0;
        return (
          <>
            <button type="button" disabled={!expandable} onClick={() => setOpen(was => toggleInSet(was, key))}
              aria-expanded={expandable ? open.has(key) : undefined}
              className="inline-flex items-center gap-1 text-left disabled:cursor-default">
              {expandable && (open.has(key) ? <ChevronDown size={12} /> : <ChevronRight size={12} />)}
              {row.name}
            </button>
            {row.note && <div className="text-warning mt-0.5">{row.note}</div>}
          </>
        );
      } },
    { key: 'amount', label: 'Сумма', align: 'right', width: 'w-36',
      cell: ({ row, part }) => part
        ? <span className="text-fg3">{money(part.amount)}</span>
        : money(row.amount) },
    { key: 'accountingOn', label: 'Учётная дата', width: 'w-28',
      cell: ({ row, part }) => part ? null : (
        <span className={`tabular-nums ${row.moved ? 'text-warning font-medium' : 'text-on-surface-variant'}`}>
          {formatDate(row.accountingOn)}
        </span>
      ) },
  ];

  return (
    <div className="space-y-2">
      {warning && (
        <p role="note" className="flex items-start gap-1.5 text-note text-warning">
          <ArrowRightLeft size={14} className="shrink-0 mt-0.5" /> {warning}
        </p>
      )}
      <DataGrid density="compact" className="max-h-72" columns={columns} rowKey={line => line.key}
        rows={lines([...summary.moved, ...calm], open)}
        tone={line => line.part === null && changed?.has(line.key) ? 'warning' : null} />
      {folded.length > 0 && (
        <button type="button" className="px-2 text-note underline decoration-dotted underline-offset-2 text-fg3"
          onClick={() => setUnfolded(true)}>
          {foldedLabel(folded)}
        </button>
      )}
    </div>
  );
}

/**
 * Строка сетки: контур или одна его доля. Доли — своими строками, а не текстом в клетке контура:
 * так их суммы стоят в колонке «Сумма», под суммой контура.
 */
interface Line {
  key: string;
  row: PostingRow;
  part: PostingPart | null;
}

function lines(rows: PostingRow[], open: ReadonlySet<string>): Line[] {
  return rows.flatMap(row => {
    const key = rowKey(row);
    const head: Line = { key, row, part: null };
    return open.has(key) ? [head, ...row.parts.map((part, i) => ({ key: `${key}#${i}`, row, part }))] : [head];
  });
}

const money = (amount: number | null) => amount === null ? '—' : formatMoney(amount);
