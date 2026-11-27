import type { ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { Card } from '@/shared/ui/Card';
import type { EditGridColumn } from '@/shared/ui/EditGrid';
import { ScheduleProductivity } from './ScheduleProductivity';

export function ScheduleWorkCards({ rows, columns, selectedWorkId }: {
  rows: ScheduleWork[]; columns: EditGridColumn<ScheduleWork>[]; selectedWorkId: string | null;
}) {
  const title = columns.find(column => column.key === 'name');
  const actions = columns.find(column => column.key === 'actions');
  const volume = columns.find(column => column.key === 'volume');
  const properties = columns.filter(column => !['name', 'actions', 'volume'].includes(column.key));
  return <ul className="grid min-w-0 gap-3" aria-label="Работы графика">
    {rows.map((work, index) => <li key={work.id} data-schedule-work-id={work.id} className="min-w-0">
      <Card className={`@container ${work.id === selectedWorkId ? 'ring-1 ring-primary' : ''}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="min-w-0 text-title-small text-on-surface">{title?.cell(work, index)}</h3>
        {actions?.cell(work, index)}
      </div>
      <dl className="grid min-w-0 grid-cols-2 items-start gap-x-4 gap-y-3 text-body-medium text-on-surface @xl:grid-cols-4">
        <div className="min-w-0">
          <dt className="text-body-small text-on-surface-variant">Объём</dt>
          <dd>{volume?.cell(work, index)}</dd>
        </div>
        {work.productivity && <div className="min-w-0 wrap-anywhere @xl:col-span-2">
          <dt className="text-body-small text-on-surface-variant">Выработка</dt>
          <dd><ScheduleProductivity value={work.productivity} unit={work.unitName} name={work.name} /></dd>
        </div>}
        {properties.map(column => <div key={column.key} className="min-w-0 wrap-anywhere">
          <dt className="text-body-small text-on-surface-variant">{column.label}</dt>
          <dd>{column.cell(work, index)}</dd>
        </div>)}
      </dl>
      </Card>
    </li>)}
  </ul>;
}
