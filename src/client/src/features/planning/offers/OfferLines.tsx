import { formatNumber } from '@/shared/format/format';
import type { OfferLine, OfferSnapshotLine } from '@/shared/api/planningOfferTypes';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { StatusText } from '@/shared/ui/StatusText';
import { MARKUP_LABELS, markupText, moneyText } from './offerFormat';

type Position = Pick<OfferLine, 'id' | 'number' | 'name' | 'volume' | 'unit' | 'price' | 'amount'>;

function positions<Row extends Position>(name: (row: Row) => React.ReactNode): DataGridColumn<Row>[] {
  return [
    { key: 'number', label: '№', cell: row => row.number ?? '—' },
    { key: 'name', label: 'Позиция', cell: name, rowHeader: true, wrap: true },
    { key: 'volume', label: 'Объём', cell: row => row.volume == null ? '—' : formatNumber(row.volume, 6), align: 'right' },
    { key: 'unit', label: 'Ед.', cell: row => row.unit ?? '—' },
  ];
}

function sales<Row extends Position>(): DataGridColumn<Row>[] {
  return [
    { key: 'price', label: 'Цена продажи', cell: row => moneyText(row.price), align: 'right' },
    { key: 'amount', label: 'Сумма', cell: row => moneyText(row.amount), align: 'right' },
  ];
}

export function OfferLines({ lines, showCost }: { lines: OfferLine[]; showCost: boolean }) {
  const columns: DataGridColumn<OfferLine>[] = [
    ...positions<OfferLine>(row => <>
      {row.name}{row.lsrSection?.name && <span className="block text-body-small text-on-surface-variant">{row.lsrSection.name}</span>}
      {row.marks.filter(mark => mark.code !== 'no-base' || showCost).map(mark =>
        <span className="block" key={mark.code}><StatusText tone="warning">{mark.text}</StatusText></span>)}
    </>),
    { key: 'markup', label: 'Действующая наценка', cell: row => `${markupText(row.markup.value)} · ${MARKUP_LABELS[row.markup.level]}` },
    ...sales<OfferLine>(),
  ];
  if (showCost && lines.some(row => row.cost != null)) columns.push(
    { key: 'cost', label: 'Себестоимость', cell: row => moneyText(row.cost?.total), align: 'right' },
    { key: 'margin', label: 'Маржа', cell: row => moneyText(row.cost?.margin), align: 'right' },
  );
  return <DataGrid columns={columns} rows={lines} rowKey={row => row.id} density="compact" />;
}

export function SnapshotLines({ lines }: { lines: OfferSnapshotLine[] }) {
  const columns: DataGridColumn<OfferSnapshotLine>[] = [
    ...positions<OfferSnapshotLine>(row => <>{row.name}
      {row.lsrSectionName && <span className="block text-body-small text-on-surface-variant">{row.lsrSectionName}</span>}
      {row.priceSource?.kind === 'none' && <span className="block"><StatusText tone="warning">Нет цены в прайс-листе</StatusText></span>}
    </>),
    { key: 'markup', label: 'Своя наценка', cell: row => markupText(row.markup) },
    ...sales<OfferSnapshotLine>(),
  ];
  return <DataGrid columns={columns} rows={lines} rowKey={row => row.id} density="compact" />;
}
