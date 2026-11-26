import { Link } from 'react-router';
import { formatMoney } from '@/shared/format/format';
import type { ProgressColumn, ProgressCosts, ProgressStrip } from '@/shared/api/progress';
import { Banner } from '@/shared/ui/Banner';
import { LinkOff, ReceiptLong, VisibilityOff } from '@/shared/ui/icons';

export function ProgressHidden({ permission }: { permission?: string | null }) {
  return <>Скрыто правами{permission && <> · нужно <code className="wrap-anywhere">{permission}</code></>}</>;
}

function StripContent({ strip }: { strip: ProgressStrip }) {
  if (strip.state === 'hidden') return <>
    {strip.code === 'estimate' && 'План по смете · '}<ProgressHidden permission={strip.permission} />
  </>;
  return strip.link
    ? <Link to={strip.link} className="text-primary underline underline-offset-2 focus-ring">{strip.text}</Link>
    : strip.text;
}

function CostsStrip({ costs, sectionId }: { costs: ProgressCosts; sectionId: string | null }) {
  if (costs.state !== 'ok') return <Banner tone="info"
    icon={costs.state === 'hidden' ? VisibilityOff : costs.state === 'not-connected' ? LinkOff : ReceiptLong}>
    <strong className="block text-label-large">Затраты</strong>
    {costs.state === 'hidden' ? <ProgressHidden permission={costs.permission} /> : costs.text ?? 'Затраты недоступны'}
  </Banner>;

  const { allocated, paid, linkedToWorks } = costs.totals;
  return <Banner tone="info" icon={ReceiptLong}>
    <strong className="block text-label-large">Затраты · {sectionId ? 'выбранный раздел' : 'вся стройка'}</strong>
    <span className="block tabular-nums">
      Разнесено <b>{formatMoney(allocated)}</b> · из них оплачено <b>{formatMoney(paid)}</b>
    </span>
    {linkedToWorks.state === 'not-tracked' && <span className="mt-1 block">
      <b>Привязано к работам · не ведётся</b><span className="block text-on-surface-variant">{linkedToWorks.reason}</span>
    </span>}
  </Banner>;
}

export function ProgressStrips({ strips, costs, sectionId, columns, partial }: {
  strips: ProgressStrip[]; costs: ProgressCosts; sectionId: string | null; columns: ProgressColumn[]; partial: boolean;
}) {
  const factNotes = strips.filter(strip => strip.code.startsWith('fact-'));
  const disconnected = strips.filter(strip => strip.state === 'not-connected');
  const other = strips.filter(strip => !strip.code.startsWith('fact-') && strip.state !== 'not-connected');
  const hiddenColumns = columns.filter(column => column.state === 'hidden'
    && !other.some(strip => strip.state === 'hidden' && strip.permission === column.permission));
  return <div className="grid min-w-0 gap-3">
    {factNotes.length > 0 && <Banner tone="warning">
      {partial && <strong className="block text-label-large">Факт неполный</strong>}
      {factNotes.map(strip => <span key={strip.code} className="block">{strip.text}</span>)}
    </Banner>}
    {disconnected.map(strip => <Banner key={strip.code} tone="info" icon={LinkOff}>{strip.text}</Banner>)}
    {hiddenColumns.length > 0 && <Banner tone="info" icon={VisibilityOff}>
      {hiddenColumns.map(column => <span key={column.code} className="block">
        {column.title} · <ProgressHidden permission={column.permission} />
      </span>)}
    </Banner>}
    <div className={other.length > 0 ? 'grid min-w-0 gap-3 @3xl:grid-cols-2 @3xl:items-start' : undefined}>
      {other.length > 0 && <Banner tone={other.some(strip => strip.state === 'partial') ? 'warning' : 'info'}>
        {other.map((strip, index) => <span key={`${strip.code}/${index}`} className="block"><StripContent strip={strip} /></span>)}
      </Banner>}
      <CostsStrip costs={costs} sectionId={sectionId} />
    </div>
  </div>;
}
