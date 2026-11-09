import type { CarryOverDecision, CarryOverItem, Distribution } from '@/shared/api/planningDistribution';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { formatQuantity } from '@/shared/format/format';
import { CATEGORY_TEXT, itemTitle, targetText, type SectionName } from './distributionView';

/**
 * Позиции отчёта переноса, ждущие решения (чип «Перенос: проверить», макет П2): решение — в строке позиции.
 * `ambiguous` — на строку-кандидат или «Не переносить»; `overflow` — открыть строку и распределить заново, затем
 * «Понятно»; `lost` — «Понятно». Без права правки — только показ.
 */
export function CarryOverList({ distribution, items, name, onDecide, onOpenLine, busy }: {
  distribution: Distribution;
  items: CarryOverItem[];
  name: SectionName;
  /** `null` — решать здесь нельзя. */
  onDecide: ((item: CarryOverItem, decision: CarryOverDecision) => void) | null;
  onOpenLine: ((rowId: string) => void) | null;
  busy: boolean;
}) {
  const lines = new Map(distribution.lines.map(l => [l.rowId, l]));
  const was = (item: CarryOverItem) => item.subject === 'rule'
    ? targetText(item.target, name)
    : item.parts.map(p => `${name(p.sectionId)} — ${formatQuantity(p.quantity.row)}`).join('; ');
  const columns: DataGridColumn<CarryOverItem>[] = [
    {
      key: 'what', label: 'Что переносили', wrap: true, rowHeader: true,
      cell: i => (
        <div className="flex flex-col gap-0.5">
          <span className="text-body-medium">{itemTitle(i)}</span>
          {i.sourceVolume !== null && (
            <span className="text-body-small text-on-surface-variant tabular-nums">
              было {formatQuantity(i.sourceVolume)}{i.sourceUnit ? ` · ${i.sourceUnit}` : ''}
              {i.newVolume !== null ? ` · стало ${formatQuantity(i.newVolume)}` : ''}
            </span>
          )}
        </div>
      ),
    },
    { key: 'was', label: 'Было', width: 'w-48', wrap: true, cell: i => <span className="text-body-medium">{was(i)}</span> },
    { key: 'state', label: 'Перенос', width: 'w-44', wrap: true, cell: i => <StatusText tone="warning">{CATEGORY_TEXT[i.category]}</StatusText> },
    {
      key: 'decide', label: 'Решение', width: 'w-64', interactive: true,
      cell: i => (onDecide ? <Decision item={i} lines={lines} busy={busy} onDecide={d => onDecide(i, d)} onOpenLine={onOpenLine} /> : null),
    },
  ];
  return <DataGrid columns={columns} rows={items} fixed rowKey={i => i.id} />;
}

function Decision({ item, lines, busy, onDecide, onOpenLine }: {
  item: CarryOverItem;
  lines: ReadonlyMap<string, Distribution['lines'][number]>;
  busy: boolean;
  onDecide: (d: CarryOverDecision) => void;
  onOpenLine: ((rowId: string) => void) | null;
}) {
  if (item.category === 'ambiguous') return (
    <div className="flex flex-wrap items-center gap-2">
      <Select density="dense" className="w-40" value="" placeholder="На строку…" disabled={busy}
        aria-label={`Перенести части позиции «${itemTitle(item)}» на строку`}
        onValueChange={rowId => onDecide({ action: 'apply', rowId })}>
        {item.candidates.map(id => {
          const line = lines.get(id);
          return <SelectItem key={id} value={id}>{line ? `${line.number} · ${line.name}` : id}</SelectItem>;
        })}
      </Select>
      <Button variant="text" disabled={busy} onClick={() => onDecide({ action: 'skip' })}>Не переносить</Button>
    </div>
  );
  return (
    <div className="flex flex-wrap items-center gap-2">
      {item.category === 'overflow' && item.rowId && onOpenLine && (
        <Button variant="text" disabled={busy} onClick={() => onOpenLine(item.rowId!)}>Распределить строку</Button>
      )}
      <Button variant="text" disabled={busy} onClick={() => onDecide({ action: 'acknowledge' })}>Понятно</Button>
    </div>
  );
}
