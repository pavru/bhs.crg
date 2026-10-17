import { CircleHelp } from 'lucide-react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { formatDate, formatMoney } from '@/shared/format/format';
import type { CorrectionLineView } from '@/shared/api/invoiceCorrection';
import { signed, type PreviewRow } from './correction';
import { placeName, type Places } from './places';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Строки книги проводок счёта: куда, каким днём и сколько — со знаком. Один вид на предпросмотр
 * исправления и историю счёта (E2, PR 6b-1, 6b-2): одно исправление до записи и после читается
 * одинаково.
 */
export function BookLines({ rows, places }: { rows: PreviewRow[]; places: Places }) {
  const line = (row: PreviewRow): CorrectionLineView | null => (row.kind === 'line' ? row.line : null);
  const columns: DataGridColumn<PreviewRow>[] = [
    { key: 'place', label: 'Куда', cell: row => { const l = line(row); return l ? where(l, places) : null; } },
    { key: 'day', label: 'День', cell: row => { const l = line(row); return l ? formatDate(l.day) : null; } },
    {
      key: 'gross', label: 'С НДС ±', align: 'right',
      cell: row => { const l = line(row); return l ? <span className="tabular-nums">{signed(l.gross, formatMoney)}</span> : null; },
    },
    {
      key: 'net', label: 'Без НДС ±', align: 'right',
      cell: row => {
        const l = line(row);
        if (!l) return null;
        return (
          <span className="inline-flex items-center gap-1 tabular-nums">
            {l.vatUnknown && (
              <Tooltip content="НДС неизвестен: сумма без НДС не посчитана точно">
                <span className="text-warning">
                  <CircleHelp size={12} aria-label="НДС неизвестен" />
                </span>
              </Tooltip>
            )}
            {signed(l.net, formatMoney)}
          </span>
        );
      },
    },
  ];
  return (
    <DataGrid density="compact" rows={rows} rowKey={row => row.key} columns={columns}
      section={row => (row.kind === 'group' ? row.title : null)} />
  );
}

/** Куда легла строка: стройка (с разделом) или статья; без цели — остаток счёта. */
function where(line: CorrectionLineView, places: Places): string {
  if (!line.constructionId && !line.articleId) return 'Не разнесено (остаток счёта)';
  return placeName({ construction: line.constructionId, section: line.sectionId, article: line.articleId }, places);
}
