import type { ReactNode } from 'react';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { EditGrid } from '@/shared/ui/EditGrid';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Tooltip } from '@/shared/ui/Tooltip';
import { durationText, employeeNames, sections, shortDate, warningLabels, type Variant, type Work } from './sample';

export function WarningMark({ work }: { work: Work }) {
  if (!work.warnings.length) return null;
  const text = work.warnings.map(warning => warningLabels[warning]).join('. ');
  return <Tooltip content={text}>
    <span className="inline-flex text-error" tabIndex={0} aria-label={text}>
      <Icon icon={Warning} size={18} aria-hidden />
    </span>
  </Tooltip>;
}

const period = (work: Work) => work.start || work.end
  ? `${shortDate(work.start)} → ${shortDate(work.end)}` : 'Сроки не заданы';

function WorkTitle({ work }: { work: Work }) {
  return <span className="flex items-start gap-2">
    <span className="font-medium">{work.name}</span><WarningMark work={work} />
  </span>;
}

export function EditFields({ work, onChange, onEmployees }: {
  work: Work; onChange: (patch: Partial<Work>) => void; onEmployees: () => void;
}) {
  return <div className="grid gap-3">
    <div className="plan-schedule-fields">
      <DateField label="Начало" value={work.start} onChange={start => onChange({ start })} />
      <DateField label="Окончание" value={work.end} onChange={end => onChange({ end })} />
    </div>
    <NumberInput label="Объём" unit={work.unit} value={work.volume} onChange={volume => onChange({ volume })} />
    <Button variant="outlined" onClick={onEmployees}>{employeeNames(work) || 'Выбрать исполнителей'}</Button>
  </div>;
}

export function WorkList({ variant, rows, selected, draft, onChoose, onChange, onEmployees, onLinks, onSave,
  onCancel, side }: {
  variant: Variant; rows: Work[]; selected: string | null; draft: Work | null; onChoose: (work: Work) => void;
  onChange: (patch: Partial<Work>) => void; onEmployees: () => void; onLinks: (work: Work) => void;
  onSave: () => void; onCancel: () => void; side: ReactNode;
}) {
  const byRow = (work: Work) => draft?.id === work.id ? draft : work;
  const compact: DataGridColumn<Work>[] = [
    { key: 'name', label: 'Работа', rowHeader: true, wrap: true, cell: work => <WorkTitle work={work} /> },
    { key: 'section', label: 'Раздел', wrap: true },
    { key: 'period', label: 'Сроки', width: 'w-32', cell: period },
    { key: 'volume', label: 'Объём', width: 'w-24', cell: work => `${work.volume} ${work.unit}` },
  ];
  if (!rows.length) return <p className="plan-schedule-panel text-body-medium text-on-surface-variant">Работ по выбранным условиям нет.</p>;

  if (variant === 'b') return <div className="plan-schedule-split">
    <DataGrid columns={compact} rows={rows} rowKey={row => row.id} density="compact" rowOpen={work => work.id === selected}
      onRowOpen={onChoose} />
    {side}
  </div>;

  if (variant === 'c') return <div className="plan-schedule-sections">
    {sections.map(section => {
      const current = rows.filter(work => work.section === section);
      if (!current.length) return null;
      return <section key={section} className="plan-schedule-panel">
        <h3 className="text-title-medium">{section} <span className="text-on-surface-variant">· {current.length}</span></h3>
        <div className="plan-schedule-cards">
          {current.map(work => <article key={work.id} className="plan-schedule-card" data-active={work.id === selected}>
            <WorkTitle work={work} />
            <p className="text-body-medium">{period(work)} · {durationText(work)}</p>
            <p className="text-body-medium text-on-surface-variant">{work.volume} {work.unit} · {employeeNames(work) || 'Без исполнителя'}</p>
            <div><Button variant="text" onClick={() => onChoose(work)}>Открыть работу</Button></div>
          </article>)}
        </div>
      </section>;
    })}
  </div>;

  return <div className="grid min-w-0 grid-cols-1 gap-3">
    <EditGrid label="Список работ" rows={rows} rowKey={row => row.id} selected={work => work.id === selected} fill
      minWidth="min-w-[68rem]" columns={[
        { key: 'name', label: 'Работа', rowHeader: true, cell: work => <div className="flex items-center gap-2">
          <Button variant="text" onClick={() => onChoose(work)}>{work.name}</Button><WarningMark work={work} /></div> },
        { key: 'section', label: 'Раздел', size: 145, cell: work => work.section },
        { key: 'volume', label: 'Объём', size: 115, cell: work => draft?.id === work.id
          ? <NumberInput label="Объём" value={byRow(work).volume} unit={work.unit} onChange={volume => onChange({ volume })} />
          : `${work.volume} ${work.unit}` },
        { key: 'start', label: 'Начало', size: 155, cell: work => draft?.id === work.id
          ? <DateField density="cell" label="Начало" value={byRow(work).start} onChange={start => onChange({ start })} /> : shortDate(work.start) },
        { key: 'end', label: 'Окончание', size: 155, cell: work => draft?.id === work.id
          ? <DateField density="cell" label="Окончание" value={byRow(work).end} onChange={end => onChange({ end })} /> : shortDate(work.end) },
        { key: 'duration', label: 'Длит.', size: 100, cell: durationText },
        { key: 'employees', label: 'Исполнители', size: 162, cell: work => draft?.id === work.id
          ? <Button variant="text" onClick={onEmployees}>{employeeNames(draft) || 'Выбрать'}</Button> : employeeNames(work) || '—' },
        { key: 'links', label: 'Связи', size: 104, cell: work =>
          <Button variant="text" onClick={() => onLinks(work)}>Связи</Button> },
      ]} />
    {draft && <div className="flex flex-wrap items-center gap-2">
      <Button variant="filled" onClick={onSave}>Сохранить работу</Button>
      <Button variant="text" onClick={onCancel}>Отмена</Button>
    </div>}
  </div>;
}
