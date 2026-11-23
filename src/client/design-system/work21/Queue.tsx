import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { CountText } from '@/shared/ui/CountText';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Apartment, Assignment, Group } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { briefDate, emptyFilters, marks, shiftName, siteName, sites, type Filters, type MarkCode, type Report, type SiteId, type Variant } from './model';

export function QueueFilters({ source, scoped, filters, onChange, canReset }: {
  source: Report[];
  scoped: Report[];
  filters: Filters;
  onChange: (next: Filters) => void;
  canReset: boolean;
}) {
  return <div className="space-y-3">
    <div className="work21-filters">
      <Select label="Стройка" density="dense" value={filters.site || 'all'} onValueChange={v => onChange({ ...filters, site: v === 'all' ? '' : v })}>
        <SelectItem value="all">Все стройки в охвате</SelectItem>
        {sites.map(s => <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>)}
      </Select>
      <Select label="Сотрудник" density="dense" value={filters.employee || 'all'} onValueChange={v => onChange({ ...filters, employee: v === 'all' ? '' : v })}>
        <SelectItem value="all">Все сотрудники</SelectItem>
        {source.map(r => <SelectItem key={r.id} value={r.id}>{r.employee}</SelectItem>)}
      </Select>
      <DateRange label="Дата смены" from={filters.from} to={filters.to} ordered
        onChange={(from, to) => onChange({ ...filters, from, to })} />
    </div>
    <div className="flex flex-wrap items-center gap-2" aria-label="Отбор по пометкам">
      {(Object.keys(marks) as MarkCode[]).map(code => {
        const count = scoped.filter(r => r.marks.some(m => m.code === code)).length;
        const { label, icon } = marks[code];
        if (count === 0) return null;
        const selected = filters.mark === code;
        return <Chip key={code} icon={icon} selected={selected} count={count}
          onClick={() => onChange({ ...filters, mark: selected ? '' : code })}>{label}</Chip>;
      })}
      {canReset && Object.values(filters).some(Boolean) && <Button variant="text" onClick={() => onChange(emptyFilters)}>Сбросить отбор</Button>}
    </div>
  </div>;
}

export function ReportMarks({ report }: { report: Report }) {
  return <div className="flex flex-wrap gap-x-3 gap-y-1">
    {report.marks.map(mark => <StatusText key={mark.code} tone={marks[mark.code].tone}
      icon={marks[mark.code].icon}>{mark.text}</StatusText>)}
  </div>;
}

const employeeColumn: DataGridColumn<Report> = {
  key: 'employee', label: 'Сотрудник', rowHeader: true, wrap: true,
  cell: row => <span className="font-medium">{row.employee}</span>,
};

const placeColumn: DataGridColumn<Report> = {
  key: 'site', label: 'Стройка', wrap: true, cell: row => siteName(row.site),
};

const shiftColumn: DataGridColumn<Report> = {
  key: 'date', label: 'Дата смены',
  cell: row => <><span>{briefDate(row.date)}</span><span className="ml-2 text-on-surface-variant">{shiftName[row.shift]}</span></>,
};

const markColumn: DataGridColumn<Report> = {
  key: 'marks', label: 'Пометки', wrap: true,
  cell: row => row.marks.length ? <ReportMarks report={row} /> : <span className="text-on-surface-variant">—</span>,
};

const compactColumns: DataGridColumn<Report>[] = [{
  ...employeeColumn, label: 'Отчёт',
  cell: row => <div className="space-y-1 py-1">
    <div className="text-body-medium font-medium">{row.employee}</div>
    <div className="text-body-small text-on-surface-variant">{siteName(row.site)} · {briefDate(row.date)} · {shiftName[row.shift]}</div>
    {row.marks.length > 0 && <ReportMarks report={row} />}
  </div>,
}];

function QueueTable({ rows, variant, selected, onOpen }: {
  rows: Report[];
  variant: Variant;
  selected?: string;
  onOpen: (report: Report) => void;
}) {
  const columns: DataGridColumn<Report>[] = [
    { ...employeeColumn, width: 'w-44' }, ...(variant === 'a' ? [placeColumn] : []), { ...shiftColumn, width: 'w-44' },
    { key: 'hours', label: 'Часы', align: 'right', width: 'w-16' },
    { key: 'lines', label: 'Строки', align: 'right', width: 'w-20' },
    markColumn,
  ];
  return <DataGrid columns={variant === 'b' ? compactColumns : columns} rows={rows} density="compact" fixed={variant === 'c'}
    rowKey={row => row.id} rowOpen={row => row.id === selected} onRowOpen={onOpen} />;
}

export function Queue({ rows, variant, empty, selected, onOpen, onCrew, onReset }: {
  rows: Report[];
  variant: Variant;
  empty: 'not-assigned' | 'nothing-pending' | 'filtered-out';
  selected?: string;
  onOpen: (report: Report) => void;
  onCrew: (site: SiteId) => void;
  onReset: () => void;
}) {
  if (!rows.length) return <EmptyState icon={<Icon icon={Assignment} size={24} />}
    title={{ 'not-assigned': 'Вы не назначены ни на одну стройку', 'nothing-pending': 'Ждущих решения нет',
      'filtered-out': 'Отбор скрыл все отчёты' }[empty]}
    action={empty === 'filtered-out' && <Button variant="filled" onClick={onReset}>Сбросить отбор</Button>} />;

  if (variant !== 'c') return <QueueTable rows={rows} variant={variant} selected={selected} onOpen={onOpen} />;

  return <div className="space-y-5">
    {sites.map(site => {
      const group = rows.filter(r => r.site === site.id);
      if (!group.length) return null;
      return <section key={site.id} aria-label={site.name} className="space-y-2">
        <header className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="flex items-center gap-2 text-title-medium">
            <Icon icon={Apartment} size={20} /> {site.name} <CountText>{group.length}</CountText>
          </h2>
          <Button variant="tonal" icon={<Icon icon={Group} size={18} />} onClick={() => onCrew(site.id)}>Сверка бригад</Button>
        </header>
        <QueueTable rows={group} variant={variant} onOpen={onOpen} />
      </section>;
    })}
  </div>;
}
