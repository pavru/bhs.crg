import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import { SCHEDULE_EDIT, SCHEDULE_READ } from '@/shared/api/planningCalendars';
import { useSchedules } from '@/shared/api/planningSchedules';
import type { ScheduleSummary } from '@/shared/api/planningScheduleTypes';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { Add, ArrowBack, CalendarMonth } from '@/shared/ui/icons';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { ConstructionCalendarPanel } from '../calendars/ConstructionCalendarPanel';
import { CreateScheduleDialog } from './CreateScheduleDialog';
import { ScheduleCard } from './ScheduleCard';

const columns: DataGridColumn<ScheduleSummary>[] = [
  { key: 'name', label: 'График', rowHeader: true, wrap: true },
  { key: 'source', label: 'Источник', cell: row => row.revisionId ? 'Редакция сметы' : 'Ручной график' },
];

export function SchedulesPanel({ constructionId }: { constructionId: string }) {
  const can = useCan();
  const read = can.permission(SCHEDULE_READ);
  const edit = read && can.permission(SCHEDULE_EDIT);
  const canReadEstimates = can.permission('plan.estimate.read');
  const tail = useParams()['*']?.split('/')[0];
  const navigate = useGuardedNavigate();
  const openCreated = useNavigate();
  const base = `/document-sets/${constructionId}/schedule`;
  const [create, setCreate] = useState<'estimate' | 'manual' | null>(null);
  const list = useSchedules(constructionId, read && !tail);
  const back = <Button variant="text" icon={<Icon icon={ArrowBack} size={18} />} onClick={() => navigate(base)}>Все графики</Button>;

  if (!read) return <ConstructionCalendarPanel constructionId={constructionId} />;

  return <section className="grid min-w-0 grid-cols-1 gap-4">
    <div className="flex flex-wrap items-center justify-between gap-2">
      {tail ? back : <h2 className="text-title-large">График стройки</h2>}
      {tail !== 'calendar' && <Button variant="outlined" icon={<Icon icon={CalendarMonth} size={18} />}
        onClick={() => navigate(`${base}/calendar`)}>Календарь стройки</Button>}
    </div>
    {tail === 'calendar' ? <ConstructionCalendarPanel constructionId={constructionId} />
      : tail ? <ScheduleCard key={`${constructionId}/${tail}`} constructionId={constructionId} id={tail} />
        : <>
          {edit && <div className="flex flex-wrap gap-2">
            {canReadEstimates && <Button variant="filled" onClick={() => setCreate('estimate')}>Построить из сметы</Button>}
            <Button variant={canReadEstimates ? 'outlined' : 'filled'} icon={<Icon icon={Add} size={18} />}
              onClick={() => setCreate('manual')}>Новый график</Button>
          </div>}
          {edit && !canReadEstimates && <p className="text-body-medium text-on-surface-variant">
            Для построения из сметы нужно право чтения смет.
          </p>}
          {list.isPending && <p role="status" className="text-body-medium">Читаем графики стройки…</p>}
          {list.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void list.refetch() }]}>
            {apiError(list.error, 'Графики стройки не прочитались.')}
          </Banner>}
          {list.data && <DataGrid columns={columns} rows={list.data} rowKey={row => row.id}
            onRowOpen={row => navigate(`${base}/${row.id}`)}
            rowLink={row => ({ to: `${base}/${row.id}`, label: row.name })} />}
        </>}
    {edit && create && (create === 'manual' || canReadEstimates) &&
      <CreateScheduleDialog constructionId={constructionId} fromEstimate={create === 'estimate'} onClose={() => setCreate(null)}
        onCreated={id => { setCreate(null); openCreated(`${base}/${id}`); }} />}
  </section>;
}
