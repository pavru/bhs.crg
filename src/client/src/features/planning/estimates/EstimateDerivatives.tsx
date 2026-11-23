import { Link, useLocation } from 'react-router';
import { useCan } from '@/shared/api/access';
import { useEstimateDerivatives, type RevisionDerivative } from '@/shared/api/planningDerivatives';
import { formatCount, formatDate, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { OfferStatusText } from '../offers/offerDisplay';
import { offerUrl } from '../offers/offerFormat';

const OFFER_READ = 'plan.offer.read';
const SCHEDULE_READ = 'plan.schedule.read';

function hidden<T>(block: { state: 'hidden'; permission: string } | { state: 'ok'; items: T[] }, allowed: boolean, permission: string) {
  return block.state === 'hidden' ? block : allowed ? block : { state: 'hidden' as const, permission };
}

function sharedColumns<Row extends RevisionDerivative>(): DataGridColumn<Row>[] {
  return [
    { key: 'date', label: 'Дата', cell: row => formatDate(row.date) },
    { key: 'coverage', label: 'Покрытие', cell: ({ coverage }) =>
      `${formatCount(coverage.covered)} из ${formatCount(coverage.total)}${coverage.percent === null ? '' : ` · ${formatNumber(coverage.percent, 2)} %`}` },
    { key: 'outdated', label: 'Редакция', cell: row => row.outdated ? <StatusText tone="warning">Устарело</StatusText> : 'Действующая' },
  ];
}

function AccessNotice({ label, action, permission }: { label: string; action: string; permission: string }) {
  return <Banner tone="info">{label} скрыты правами. Нужно право «{action}» (<code>{permission}</code>).</Banner>;
}

export function EstimateDerivatives({ estimateId, revisionId }: { estimateId: string; revisionId: string }) {
  const can = useCan();
  const canRead = can.permission('plan.estimate.read');
  const query = useEstimateDerivatives(estimateId, canRead);
  const { pathname, search } = useLocation();
  if (!canRead) return null;
  const revision = query.isError ? undefined : query.data?.revisions.find(row => row.revisionId === revisionId);
  const offers = revision && hidden(revision.offers, can.permission(OFFER_READ), OFFER_READ);
  const schedules = revision && hidden(revision.schedules, can.permission(SCHEDULE_READ), SCHEDULE_READ);
  return <section className="grid min-w-0 grid-cols-1 gap-3 pt-2" aria-label="Производные сметы">
    <h3 className="text-title-medium">Производные</h3>
    {query.isPending && <p role="status" className="text-body-medium">Читаем производные…</p>}
    {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
      {apiError(query.error, 'Производные не прочитались.')}
    </Banner>}
    {query.data && !query.isError && !revision && <p className="text-body-medium">Производные этой редакции пока недоступны.</p>}
    {offers && <section className="grid min-w-0 grid-cols-1 gap-2" aria-label="КП редакции">
      <h4 className="text-title-small">Коммерческие предложения</h4>
      {offers.state === 'hidden' ? <AccessNotice label="КП" action="Читать коммерческие предложения" permission={offers.permission} />
        : offers.items.length === 0 ? <p className="text-body-medium">КП по этой редакции пока нет.</p>
        : <DataGrid rows={offers.items} rowKey={row => `${row.id}:${row.currentVersion}`} density="compact" columns={[
          { key: 'number', label: 'КП', rowHeader: true, cell: row => <>
            <Link className="text-primary hover:underline focus-ring"
              to={offerUrl(row.id, row.historical ? row.currentVersion : undefined)}
              state={{ estimatePath: pathname + search, estimateRevisionId: revisionId }}
              aria-label={`Открыть ${row.number}, версия ${row.currentVersion}`}>{row.number}</Link>
            <span className="block text-body-small text-on-surface-variant">Версия {row.currentVersion}{row.historical ? ' · История' : ''}</span>
          </> },
          { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
          ...sharedColumns<(typeof offers.items)[number]>(),
          { key: 'sent', label: 'Отправлено', cell: row => row.sentOn ? formatDate(row.sentOn) : '—' },
        ]} />}
    </section>}
    {schedules && <section className="grid min-w-0 grid-cols-1 gap-2" aria-label="Графики редакции">
      <h4 className="text-title-small">Графики</h4>
      {schedules.state === 'hidden' ? <AccessNotice label="Графики" action="Читать графики" permission={schedules.permission} />
        : schedules.items.length === 0 ? <p className="text-body-medium">Графиков по этой редакции пока нет.</p>
        : <DataGrid rows={schedules.items} rowKey={row => `${row.id}:${row.currentVersion}`} density="compact" columns={[
          { key: 'number', label: 'График', rowHeader: true, cell: row => row.number },
          { key: 'status', label: 'Статус', cell: row => row.status },
          ...sharedColumns<RevisionDerivative>(),
        ]} />}
    </section>}
  </section>;
}
