import { useEffect, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import { useConstructionDirectory } from '@/shared/api/constructions';
import { useEmployees } from '@/shared/api/employees';
import type { ReviewEmptyReason, ReviewQueuePage as QueuePage, ReviewQueueRow } from '@/shared/api/work';
import { REVIEW_QUEUE_LIMIT, useReviewQueue, useReviewSites, type ReviewQueueFilters } from '@/shared/api/workReview';
import { formatCount, formatDate, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { CountText } from '@/shared/ui/CountText';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Assignment, ChevronLeft, ChevronRight } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { reviewMarkStyle, useReviewMarkFilters } from './reviewMarks';

const emptyTitle: Record<ReviewEmptyReason, string> = {
  'not-assigned': 'Вы не назначены ни на одну стройку',
  'not-linked': 'Учётная запись не связана с сотрудником',
  'directory-missing': 'Справочник сотрудников не заведён',
  'nothing-pending': 'Ждущих решения нет',
  'filtered-out': 'Отбор скрыл все отчёты',
};

type Option = { id: string; name: string };
const filterKeys = ['constructionId', 'employeeId', 'from', 'to', 'mark'] as const;

function namedOptions(directory: Option[], ids: Array<string | undefined>, fallback: string) {
  const names = new Map(directory.map(item => [item.id, item.name]));
  for (const id of ids) {
    if (id && !names.has(id)) names.set(id, `${fallback} · ${id}`);
  }
  return names;
}

function Choice({ label, any, value, options, onChange }: {
  label: string; any: string; value?: string; options: Map<string, string>; onChange: (value: string) => void;
}) {
  return <Select label={label} density="dense" value={value ?? ''} emptyItem={any} onValueChange={onChange}>
    {[...options].map(([id, name]) => <SelectItem key={id} value={id}>{name}</SelectItem>)}
  </Select>;
}

function QueueGrid({ data, sites, employees }: {
  data: QueuePage; sites: Map<string, string>; employees: Map<string, string>;
}) {
  const columns: DataGridColumn<ReviewQueueRow>[] = [
    { key: 'employeeId', label: 'Сотрудник', rowHeader: true, wrap: true, cell: row => employees.get(row.employeeId) },
    { key: 'constructionId', label: 'Стройка', wrap: true, cell: row => sites.get(row.constructionId) },
    { key: 'date', label: 'Дата смены', cell: row => <>
      {formatDate(row.date)}<span className="ml-2 text-on-surface-variant">{row.shift === 'night' ? '☾ ночь' : 'день'}</span>
    </> },
    { key: 'hours', label: 'Часы', align: 'right', cell: row => row.hours == null ? '—' : formatNumber(row.hours) },
    { key: 'lines', label: 'Строки', align: 'right', cell: row => formatCount(row.lines) },
    { key: 'marks', label: 'Пометки', wrap: true, cell: row => row.marks.length === 0 ? '—'
      : <div className="flex flex-wrap gap-x-3 gap-y-1">{row.marks.map(mark => {
        const { icon, tone } = reviewMarkStyle(mark.code);
        return <StatusText key={mark.code} icon={icon} tone={tone}>{mark.text}</StatusText>;
      })}</div> },
  ];
  return <DataGrid columns={columns} rows={data.items} rowKey={row => row.id} />;
}

function QueuePager({ data, onChange }: { data: QueuePage; onChange: (offset: number) => void }) {
  const { offset, limit, total, items } = data;
  if (offset === 0 && items.length >= total) return null;
  return <div className="flex flex-wrap items-center justify-end gap-2 text-body-small text-on-surface-variant">
    <IconButton icon={ChevronLeft} label="Предыдущая страница" disabled={offset === 0}
      onClick={() => onChange(Math.max(0, offset - limit))} />
    <span>{formatCount(offset + 1)}–{formatCount(offset + items.length)} из {formatCount(total)}</span>
    <IconButton icon={ChevronRight} label="Следующая страница" disabled={offset + items.length >= total}
      onClick={() => onChange(offset + limit)} />
  </div>;
}

export function ReviewQueuePage() {
  useDocumentTitle('Приёмка');
  const [params, setParams] = useSearchParams();
  const written = useRef(params);
  useEffect(() => { written.current = params; }, [params]);
  const { permission } = useCan();
  const filters: ReviewQueueFilters = {
    constructionId: params.get('constructionId') || undefined,
    employeeId: params.get('employeeId') || undefined,
    from: params.get('from') || undefined,
    to: params.get('to') || undefined,
    mark: params.get('mark') || undefined,
  };
  const requestedOffset = Number(params.get('offset'));
  const offset = Number.isInteger(requestedOffset) && requestedOffset >= 0 && requestedOffset <= 2_147_483_647 ? requestedOffset : 0;
  const query = useReviewQueue({ ...filters, offset, limit: REVIEW_QUEUE_LIMIT });
  const data = query.data;
  const allSites = data?.boundary.code === 'all' && permission('core.constructions.read');
  const needsOwnSites = !!data && !allSites && data.boundary.sites !== 0;
  const canReadEmployees = permission('core.employees.read');
  const siteDirectory = useConstructionDirectory(allSites);
  const ownSites = useReviewSites(needsOwnSites);
  const employeeDirectory = useEmployees(canReadEmployees);
  const rows = data?.items ?? [];
  const sites = namedOptions((allSites ? siteDirectory.data : ownSites.data) ?? [],
    [filters.constructionId, ...rows.map(row => row.constructionId)], 'Стройка');
  const employees = namedOptions((employeeDirectory.data?.items ?? []).map(item => ({ id: item.id, name: item.displayName })),
    [filters.employeeId, ...rows.map(row => row.employeeId)], 'Сотрудник');
  const { constructionId, employeeId, from, to } = filters;
  const markFilters = useReviewMarkFilters(data, { constructionId, employeeId, from, to });
  const hasFilters = filterKeys.some(key => !!filters[key]);
  const directoryFailed = (allSites && siteDirectory.isError) || (needsOwnSites && ownSites.isError)
    || (canReadEmployees && employeeDirectory.isError);

  // DateRange может поправить границы в том же событии, в котором меняется соседний отбор.
  function update(changes: Partial<Record<(typeof filterKeys)[number] | 'offset', string>>) {
    const next = new URLSearchParams(written.current);
    next.delete('offset');
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value); else next.delete(key);
    }
    written.current = next;
    setParams(next, { replace: true });
  }
  const reset = () => update({ constructionId: '', employeeId: '', from: '', to: '', mark: '' });
  const resetButton = <Button variant="text" onClick={reset}>Сбросить отбор</Button>;
  const gridIcon = <Icon icon={Assignment} size={24} />;

  return <div className="h-full min-w-0 overflow-auto px-6 py-4 space-y-4">
    <PageHeader title="Приёмка" subtitle={data?.boundary.text}
      badge={!!data?.total && <CountText>Ждут решения · {formatCount(data.total)}</CountText>} />
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-56 min-w-0 max-w-full">
        <Choice label="Стройка" any="Все стройки в охвате" value={constructionId} options={sites}
          onChange={value => update({ constructionId: value })} />
      </div>
      <div className="w-60 min-w-0 max-w-full">
        <Choice label="Сотрудник" any="Все сотрудники" value={employeeId} options={employees}
          onChange={value => update({ employeeId: value })} />
      </div>
      <DateRange label="Дата смены" from={from ?? ''} to={to ?? ''} ordered className="w-100 max-w-full"
        onChange={(nextFrom, nextTo) => update({ from: nextFrom, to: nextTo })} />
    </div>
    {(markFilters.items.length > 0 || (hasFilters && data?.reason !== 'filtered-out')) &&
      <div role="group" aria-label="Отбор по пометкам" className="flex flex-wrap items-center gap-2">
        {markFilters.items.map(({ code, label, count, icon }) => <Chip key={code} icon={icon} count={count}
          selected={filters.mark === code} onClick={() => update({ mark: filters.mark === code ? '' : code })}>{label}</Chip>)}
        {hasFilters && data?.reason !== 'filtered-out' && resetButton}
      </div>}
    {directoryFailed && <Banner tone="warning">Справочники для очереди загрузились не полностью.</Banner>}
    {markFilters.unavailable && <Banner tone="warning">Не удалось загрузить подписи некоторых пометок.</Banner>}
    {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
      {apiError(query.error, 'Не удалось загрузить очередь приёмки.')}
    </Banner>}
    {query.isPending && <p role="status" className="text-body-medium text-on-surface-variant">Загрузка очереди приёмки…</p>}
    {data?.reason && <EmptyState icon={gridIcon} title={emptyTitle[data.reason]}
      action={data.reason === 'filtered-out' && <Button variant="filled" onClick={reset}>Сбросить отбор</Button>} />}
    {data && !data.reason && (data.items.length > 0 ? <>
      <QueueGrid data={data} sites={sites} employees={employees} />
      <QueuePager data={data} onChange={next => update({ offset: next ? String(next) : '' })} />
    </> : <EmptyState icon={gridIcon} title="На этой странице нет отчётов"
      action={<Button variant="tonal" onClick={() => update({ offset: '' })}>К началу очереди</Button>} />)}
  </div>;
}
