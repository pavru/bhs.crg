import { useState } from 'react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { apiError } from '@/shared/utils/apiError';
import { formatDate, formatDateTime, formatMoney } from '@/shared/format/format';
import type { InvoiceView } from '@/shared/api/invoices';
import { useInvoiceBook, type InvoiceBookEntry } from '@/shared/api/invoiceCorrection';
import { bookAuthor, bookEmpty, bookEntryRows, bookKindTitle, bookTotal } from './correction';
import { usePlaces } from './places';
import { BookLines } from './BookLines';

/**
 * «Исправления» — история счёта, его книга проводок (E2, issue #1100, PR 6b-2): кто, когда и почему
 * сдвинул деньги оплаченного счёта. Только у счёта с книгой: у остальных истории нет, и пустая секция
 * читалась бы «исправлений не было — проверьте».
 *
 * <p>Новые записи — сверху: историю открывают, чтобы узнать, что случилось последним. Щелчок по записи
 * показывает её строки — тем же видом, что предпросмотр исправления.</p>
 */
export function CorrectionHistory({ view, gone }: { view: InvoiceView; gone: boolean }) {
  // Удалённый счёт истории не спрашивает: адрес ответил бы 404, и под «счёта больше нет» встало бы
  // второе, красное «не загрузилась».
  const shown = (view.book ?? 0) > 0 && !gone;
  const book = useInvoiceBook(view, shown);
  const places = usePlaces();
  const [open, setOpen] = useState<number | null>(null);
  if (!shown) return null;

  const entries = [...(book.data ?? [])].reverse();
  const selected = entries.find(e => e.seq === open) ?? null;
  const columns: DataGridColumn<InvoiceBookEntry>[] = [
    { key: 'at', label: 'Когда', cell: e => formatDateTime(e.at) },
    { key: 'author', label: 'Кто', cell: e => bookAuthor(e) },
    { key: 'kind', label: 'Вид', cell: e => (e.version != null ? `${bookKindTitle(e.kind)} (версия ${e.version})` : bookKindTitle(e.kind)) },
    { key: 'reason', label: 'Причина', wrap: true, cell: e => e.reason ?? '—' },
    { key: 'day', label: 'День', cell: e => formatDate(e.day) },
    { key: 'total', label: 'Итог ±', align: 'right', cell: e => <span className="tabular-nums">{bookTotal(e, formatMoney)}</span> },
  ];

  return (
    <section className="space-y-3" aria-label="Исправления">
      <h2 className="text-base font-semibold">Исправления</h2>
      {book.isPending && <p className="text-xs text-fg3">Загружаем историю счёта…</p>}
      {book.error != null && <p className="text-xs text-danger">История счёта не загрузилась: {apiError(book.error)}</p>}
      {book.data && (
        <>
          <DataGrid density="compact" rows={entries} rowKey={e => String(e.seq)} columns={columns}
            onRowOpen={e => setOpen(o => (o === e.seq ? null : e.seq))} rowOpen={e => e.seq === open} />
          {selected && (
            <div className="space-y-2">
              <p className="text-xs text-fg2">
                {bookKindTitle(selected.kind)} от {formatDate(selected.day)}
                {selected.lines.length === 0 ? ` — ${bookEmpty(selected.kind)}` : ':'}
              </p>
              {selected.lines.length > 0 && (
                <BookLines places={places} rows={bookEntryRows(selected, formatDate)} />
              )}
            </div>
          )}
        </>
      )}
    </section>
  );
}
