import type { ScheduleLink, ScheduleWarning, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { formatDate, formatNumber } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { DateInput } from '@/shared/ui/DateInput';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { Icon } from '@/shared/ui/Icon';
import { Delete, Edit, Warning } from '@/shared/ui/icons';
import { NumberInput } from '@/shared/ui/NumberInput';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { Tooltip } from '@/shared/ui/Tooltip';
import { ScheduleFactCell } from './ScheduleFact';
import { ScheduleWorkCards } from './ScheduleWorkCards';
import { assigneeText, sectionName, volumeText, type ScheduleSection } from './scheduleDisplay';
import type { WorkDraft } from './workDraft';

export function ScheduleWarningMark({ warnings }: { warnings: ScheduleWarning[] }) {
  if (!warnings.length) return null;
  const text = [...new Set(warnings.map(warning => warning.text))].join(' ');
  return <Tooltip content={text}>
    <span role="img" aria-label={text} tabIndex={0} className="inline-flex shrink-0 text-error">
      <Icon icon={Warning} size={18} aria-hidden />
    </span>
  </Tooltip>;
}

export function ScheduleWorksGrid({ rows, sections, warnings, links, factState, editingId, selectedWorkId, draft, canEdit, canReadEmployees, busy,
  onOpen, onChange, onIncomplete, onEmployees, onLinks, onDelete }: {
  rows: ScheduleWork[]; sections: ScheduleSection[]; warnings: Map<string, ScheduleWarning[]>;
  links: ScheduleLink[];
  factState?: NonNullable<ScheduleWork['fact']>['state'];
  editingId: string | null; selectedWorkId: string | null; draft: WorkDraft | null;
  canEdit: boolean; canReadEmployees: boolean; busy: boolean;
  onOpen: (work: ScheduleWork) => void; onChange: (patch: Partial<WorkDraft>) => void;
  onIncomplete: (field: 'start' | 'end', hasError: boolean) => void; onEmployees: () => void;
  onLinks: (work: ScheduleWork) => void; onDelete: (work: ScheduleWork) => void;
}) {
  const current = (work: ScheduleWork) => work.id === editingId ? draft : null;
  const locked = draft !== null || busy;
  const incoming = new Map<string, number>();
  for (const link of links) incoming.set(link.successorId, (incoming.get(link.successorId) ?? 0) + 1);
  const columns: EditGridColumn<ScheduleWork>[] = [
    { key: 'name', label: 'Работа', rowHeader: true, cell: work =>
      <span className="flex items-center gap-2">
        {canEdit && (!locked || work.id === editingId) ? <Button variant="text" multiline disabled={busy}
          onClick={() => onOpen(work)}>{work.name}</Button> : <span>{work.name}</span>}
        <ScheduleWarningMark warnings={warnings.get(work.id) ?? []} />
      </span> },
    { key: 'section', label: 'Раздел', size: 144, cell: work => sectionName(work.sectionId, sections) },
    { key: 'volume', label: 'Объём', size: 116, cell: work => {
      const edit = current(work);
      return edit ? <NumberInput label="Объём" value={edit.volume} unit={work.unitName} disabled={busy}
        onChange={volume => onChange({ volume })} /> : volumeText(work.volume, work.unitName);
    } },
    ...(factState && factState !== 'not-connected' ? [{
      key: 'fact', label: factState === 'partial' ? 'Факт · включённый объём' : 'Факт по учёту',
      size: 144, cell: (work: ScheduleWork) => <ScheduleFactCell fact={work.fact} unitName={work.unitName} />,
    }] : []),
    ...(['start', 'end'] as const).map(field => {
      const label = field === 'start' ? 'Начало' : 'Окончание';
      return { key: field, label, size: 152, cell: (work: ScheduleWork) => {
        const edit = current(work);
        return edit ? <DateInput label={label} density="cell" value={edit[field]} disabled={busy}
          onChange={value => onChange({ [field]: value })}
          onIncomplete={message => onIncomplete(field, !!message)} /> : work[field] ? formatDate(work[field]) : '—';
      } };
    }),
    { key: 'duration', label: 'Рабочих дней', size: 104, cell: work =>
      work.durationWorkingDays == null ? '—' : formatNumber(work.durationWorkingDays) },
    { key: 'employees', label: 'Исполнители', size: 178, cell: work => {
      const edit = current(work);
      if (!edit) return assigneeText(work.employeeIds, work.assignees);
      const text = edit.employeeIds.length ? assigneeText(edit.employeeIds, edit.assignees) : 'Выбрать';
      return canReadEmployees ? <Button variant="text" multiline disabled={busy} onClick={onEmployees}
        aria-label="Выбрать исполнителей">{text}</Button> : assigneeText(edit.employeeIds, edit.assignees);
    } },
    { key: 'links', label: 'Связи', size: 110, cell: work => {
      const count = incoming.get(work.id) ?? 0;
      return canEdit || count ? <Button variant="text" disabled={locked} onClick={() => onLinks(work)}
        aria-label={`Предшественники: ${work.name}`}>{count ? `Связи · ${count}` : 'Связи'}</Button> : '—';
    } },
  ];
  if (canEdit) columns.push({
    key: 'actions', label: 'Действия', size: 86,
    cell: work => locked ? null : <RowActionsMenu ariaLabel={`Действия работы «${work.name}»`} actions={[
      { key: 'edit', label: 'Изменить', icon: Edit, onSelect: () => onOpen(work) },
      { key: 'delete', label: 'Удалить', icon: Delete, danger: true, onSelect: () => onDelete(work) },
    ]} />,
  });
  if (!draft && rows.some(work => work.productivity))
    return <ScheduleWorkCards rows={rows} columns={columns} selectedWorkId={selectedWorkId} />;

  return <EditGrid label="Работы графика" rows={rows} rowKey={row => row.id} columns={columns} fill
    empty="Работ по выбранным условиям нет."
    rowData={work => ({ 'data-schedule-work-id': work.id })}
    selected={work => work.id === (editingId ?? selectedWorkId)} />;
}
