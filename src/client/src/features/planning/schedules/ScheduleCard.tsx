import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { useCan } from '@/shared/api/access';
import { useGetConstruction } from '@/shared/api/constructions';
import { SCHEDULE_EDIT, SCHEDULE_READ } from '@/shared/api/planningCalendars';
import { scheduleRefusal, useSchedule, useWriteScheduleWork } from '@/shared/api/planningSchedules';
import type { Schedule, ScheduleWarning, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { useLocale } from '@/shared/hooks/useLocale';
import { useMedia } from '@/shared/hooks/useMedia';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { ManualScheduleWorkDialog } from './ManualScheduleWorkDialog';
import { RebuildByDistributionDialog } from './RebuildByDistributionDialog';
import { ScheduleEmployeeDialog } from './ScheduleEmployeeDialog';
import { ScheduleFactNotice } from './ScheduleFact';
import { ScheduleGantt } from './ScheduleGantt';
import { ScheduleLinksDialog } from './ScheduleLinksDialog';
import { SchedulePrintDialog } from './SchedulePrintDialog';
import { ScheduleWorksGrid } from './ScheduleWorksGrid';
import { skippedText, warningFilters, type ScheduleSection } from './scheduleDisplay';
import { draftChanged, draftOf, keepEdits, workInput, type WorkDraft } from './workDraft';
import { scheduleTableLink } from '../planTableLinks';

const ALL_SECTIONS = 'all';
const WHOLE_SITE = 'whole-site';
const STALE = 'Работу тем временем изменили. Перечитайте график. Ваш ввод сохранён.';
const VIEW_ID = 'schedule-view';
type View = 'list' | 'gantt';

export function ScheduleCard({ constructionId, id }: { constructionId: string; id: string }) {
  const site = useGetConstruction(constructionId);
  const query = useSchedule(constructionId, id);
  if (!query.data) {
    if (query.isPending) return <p role="status" className="text-body-medium">Читаем график…</p>;
    return <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
      {scheduleRefusal(query.error, 'График не прочитался.').message}
    </Banner>;
  }
  if (query.data.constructionId !== constructionId) return <Banner tone="error">График относится к другой стройке.</Banner>;
  return <ScheduleContents schedule={query.data} sections={site.data?.sections ?? []} reload={query.refetch} />;
}

function ScheduleContents({ schedule, sections, reload }: {
  schedule: Schedule; sections: ScheduleSection[];
  reload: () => Promise<{ isSuccess: boolean; data?: Schedule }>;
}) {
  const can = useCan();
  const guardedClick = useGuardedNavClick();
  const canPrint = can.permission(SCHEDULE_READ);
  const canEdit = can.permission(SCHEDULE_EDIT);
  const canReadEmployees = can.permission('core.employees.read');
  const canReadCatalog = can.permission('core.catalog.read');
  const [locale] = useLocale();
  const wide = useMedia('(min-width: 1280px)');
  const [view, setView] = useState<View>('list');
  const visibleView = wide ? view : 'list';
  const [selectedWorkId, setSelectedWorkId] = useState<string | null>(null);
  const table = useRef<HTMLDivElement>(null);
  const [section, setSection] = useState(ALL_SECTIONS);
  const [warningCode, setWarningCode] = useState<string | null>(null);
  const [edit, setEdit] = useState<{ baseline: ScheduleWork; draft: WorkDraft; locale: string } | null>(null);
  const [invalid, setInvalid] = useState({ start: false, end: false });
  const [refused, setRefused] = useState<string | null>(null);
  const [rereading, setRereading] = useState(false);
  const [review, setReview] = useState('');
  const [dialog, setDialog] = useState<'rebuild' | 'add' | 'employees' | 'print' | null>(null);
  const [deleting, setDeleting] = useState<ScheduleWork | null>(null);
  const [linkWorkId, setLinkWorkId] = useState<string | null>(null);
  const write = useWriteScheduleWork(schedule.constructionId, schedule.id);
  const pending = write.isPending;
  const input = edit ? workInput(edit.draft, edit.locale, edit.baseline) : null;
  const hasInvalidInput = !input || invalid.start || invalid.end;
  const dirty = !!edit && (draftChanged(draftOf(edit.baseline, edit.locale), edit.draft) || invalid.start || invalid.end);
  const refusal = edit && write.error ? scheduleRefusal(write.error, 'Работа не сохранилась.') : null;
  const stale = refusal?.kind === 'conflict' || refused !== null;
  const editingId = canEdit ? edit?.baseline.id ?? null : null;
  const tableLink = scheduleTableLink(schedule.id, section === ALL_SECTIONS ? undefined : section === WHOLE_SITE ? null : section,
    warningCode);

  useEffect(() => {
    const workId = editingId ?? selectedWorkId;
    if (!workId || visibleView !== 'list') return;
    const row = [...(table.current?.querySelectorAll<HTMLElement>('[data-schedule-work-id]') ?? [])]
      .find(item => item.dataset.scheduleWorkId === workId);
    if (!row) return;
    const field = editingId && row.querySelector<HTMLInputElement>('input:enabled');
    if (field) {
      field.focus();
      return;
    }
    row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    (row.querySelector<HTMLElement>('button, [tabindex="0"]') ?? table.current)?.focus({ preventScroll: true });
  }, [editingId, selectedWorkId, visibleView]);

  function start(work: ScheduleWork) {
    if (edit) return;
    setSelectedWorkId(work.id);
    setEdit({ baseline: work, draft: draftOf(work, locale), locale });
    setInvalid({ start: false, end: false });
    setRefused(null);
    setReview('');
    write.reset();
  }

  function change(patch: Partial<WorkDraft>) {
    setEdit(current => current && { ...current, draft: { ...current.draft, ...patch } });
  }

  function closeEdit() {
    setEdit(null);
    setRefused(null);
    setReview('');
    write.reset();
  }

  async function save() {
    if (!canEdit || !edit || !input || hasInvalidInput || stale || pending || rereading) return false;
    try {
      await write.mutateAsync({ kind: 'edit', id: edit.baseline.id, version: edit.baseline.version, input });
      closeEdit();
      return true;
    } catch { return false; }
  }

  async function reread() {
    if (!edit || rereading) return;
    setRereading(true);
    try {
      const result = await reload();
      if (!result.isSuccess) {
        setRefused('График не прочитался. Ваш ввод сохранён.');
        return;
      }
      const fresh = result.data?.works.find(work => work.id === edit.baseline.id);
      if (!fresh) {
        setRefused('Работу удалили. Ваш ввод остался в строке.');
        return;
      }
      setEdit(current => current && {
        ...current, baseline: fresh,
        draft: keepEdits(draftOf(current.baseline, current.locale), draftOf(fresh, current.locale), current.draft),
      });
      setRefused(null);
      setReview('График перечитан. Проверьте значения и сохраните работу.');
      write.reset();
    } finally { setRereading(false); }
  }

  const warnings = new Map<string, ScheduleWarning[]>();
  for (const warning of schedule.warnings) {
    const rows = warnings.get(warning.workId) ?? [];
    rows.push(warning);
    warnings.set(warning.workId, rows);
  }
  for (const work of schedule.works) {
    if (work.warnings?.length) warnings.set(work.id, work.warnings);
  }
  const group = schedule.works.filter(work => section === ALL_SECTIONS
    || (section === WHOLE_SITE ? work.sectionId === null : work.sectionId === section));
  const filters = warningFilters(group.flatMap(work => warnings.get(work.id) ?? []));
  const filtered = warningCode === null ? group : group.filter(work => warnings.get(work.id)?.some(w => w.code === warningCode));
  const editingWork = edit && (schedule.works.find(work => work.id === edit.baseline.id) ?? { ...edit.baseline, fact: null });
  const rows = editingWork && !filtered.some(work => work.id === editingWork.id) ? [...filtered, editingWork] : filtered;
  const fact = schedule.works.find(work => work.fact)?.fact;
  const calendarGaps = [...new Set(group.flatMap(work => warnings.get(work.id) ?? [])
    .filter(warning => warning.code === 'calendar-missing').map(warning => warning.text))];
  const distribution = schedule.marks.find(mark => mark.code === 'distribution-changed');
  const linkWork = schedule.works.find(work => work.id === linkWorkId);
  const viewBody = visibleView === 'gantt' ? <ScheduleGantt constructionId={schedule.constructionId} scheduleId={schedule.id} rows={rows}
    links={schedule.links} warnings={warnings} onOpen={workId => { setSelectedWorkId(workId); setView('list'); }} />
    : <div ref={table} tabIndex={-1} className="min-w-0">
      <ScheduleWorksGrid rows={rows} sections={sections} warnings={warnings} links={schedule.links}
        factState={fact?.state}
        canEdit={canEdit} canReadEmployees={canReadEmployees}
        editingId={editingId} draft={canEdit ? edit?.draft ?? null : null}
        selectedWorkId={selectedWorkId} busy={pending || rereading} onOpen={start} onChange={change}
        onIncomplete={(field, hasError) => setInvalid(current => current[field] === hasError ? current : { ...current, [field]: hasError })}
        onEmployees={() => setDialog('employees')} onLinks={work => setLinkWorkId(work.id)}
        onDelete={work => { write.reset(); setDeleting(work); }} />
    </div>;

  return <div className="grid min-w-0 grid-cols-1 gap-4">
    <LeaveGuard active={canEdit && dirty} saving={pending} save={save}
      message="Сохранить изменения работы перед переходом?"
      saveBlocked={stale ? STALE : hasInvalidInput ? 'Проверьте объём и даты.' : undefined} />
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div><h2 className="text-headline-small">{schedule.name}</h2>
        <p className="text-body-medium text-on-surface-variant">{schedule.revisionId ? 'Редакция сметы' : 'Ручной график'}</p>
      </div>
      {canPrint && <Link className="self-center text-body-medium text-primary hover:underline"
        to={tableLink} onClick={guardedClick(tableLink)}>Открыть таблицей</Link>}
      {canEdit && canReadCatalog && <Button variant="outlined" disabled={!!edit || pending} onClick={() => setDialog('add')}>
        Добавить работу
      </Button>}
      {canPrint && <Button variant="outlined" disabled={dirty || pending || rereading} onClick={() => setDialog('print')}>
        Печать списка…
      </Button>}
      {wide && <Tabs<View> id={VIEW_ID} aria-label="Вид графика" variant="segmented" value={visibleView}
        items={[{ value: 'list', label: 'Список' }, { value: 'gantt', label: 'Диаграмма', disabled: !!edit }]}
        onChange={setView} />}
    </div>
    {canEdit && !canReadCatalog && <p className="text-body-medium text-on-surface-variant">
      Для добавления работы нужно право чтения классификатора видов работ.
    </p>}
    {distribution && <Banner tone="warning" actions={canEdit && !edit ? [
      { label: 'Предпросмотр перестроения', onClick: () => setDialog('rebuild') },
    ] : undefined}>{distribution.text}</Banner>}
    {schedule.skipped.unmapped > 0 && <p className="text-body-medium text-on-surface-variant">{skippedText(schedule.skipped.unmapped)}</p>}
    {visibleView === 'list' && <ScheduleFactNotice fact={fact} diagnostics={schedule.factDiagnostics} />}
    {calendarGaps.length > 0 && <Banner tone="warning"><ul>{calendarGaps.map(text => <li key={text}>{text}</li>)}</ul></Banner>}
    <div className="flex flex-wrap items-center gap-2">
      <div className="w-56 max-w-full"><Select label="Раздел" value={section} disabled={!!edit} onValueChange={value => {
        setSection(value);
        setWarningCode(null);
      }}>
        <SelectItem value={ALL_SECTIONS}>Все разделы</SelectItem>
        {sections.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
        <SelectItem value={WHOLE_SITE}>По стройке в целом</SelectItem>
      </Select></div>
      {(!!filters.length || warningCode) && <div aria-label="Фильтр предупреждений" className="flex flex-wrap items-center gap-1.5">
        <Button variant={warningCode === null ? 'tonal' : 'text'} aria-pressed={warningCode === null} disabled={!!edit}
          onClick={() => setWarningCode(null)}>Все работы</Button>
        {filters.map(filter => <Button key={filter.code} aria-pressed={warningCode === filter.code}
          variant={warningCode === filter.code ? 'tonal' : 'outlined'} disabled={!!edit} onClick={() => setWarningCode(filter.code)}>
          {filter.label} · {filter.count}
        </Button>)}
      </div>}
    </div>
    {wide ? <TabPanel id={VIEW_ID} value={visibleView}>{viewBody}</TabPanel> : viewBody}
    {canEdit && edit && <>
      {!canReadEmployees && <p className="text-body-medium text-on-surface-variant">
        Для выбора исполнителей нужно право чтения справочника сотрудников.
      </p>}
      {stale ? <Banner tone="warning" actions={rereading ? undefined : [{ label: 'Перечитать', onClick: () => void reread() }]}>
        {refused ?? STALE}
      </Banner> : refusal && <Banner tone="error">{refusal.message}</Banner>}
      {review && !stale && <p role="status" className="text-body-medium">{review}</p>}
      {!input && <p className="text-body-medium text-error" role="alert">Проверьте объём и порядок дат.</p>}
      <div className="flex flex-wrap gap-2">
        <Button variant="filled" loading={pending} disabled={!dirty || hasInvalidInput || stale || rereading}
          onClick={() => void save()}>Сохранить работу</Button>
        <Button variant="text" disabled={pending || rereading} onClick={closeEdit}>Отмена</Button>
      </div>
    </>}
    {canEdit && dialog === 'rebuild' && <RebuildByDistributionDialog id={schedule.id} sections={sections} onClose={() => setDialog(null)} />}
    {canPrint && dialog === 'print' && <SchedulePrintDialog schedule={schedule} onClose={() => setDialog(null)} />}
    {canEdit && canReadCatalog && dialog === 'add' && <ManualScheduleWorkDialog constructionId={schedule.constructionId}
      id={schedule.id} sections={sections} canReadEmployees={canReadEmployees} onClose={() => setDialog(null)} />}
    {canEdit && canReadEmployees && edit && dialog === 'employees' && <ScheduleEmployeeDialog
      ids={edit.draft.employeeIds} assignees={edit.draft.assignees} onClose={() => setDialog(null)}
      onChoice={(employeeIds, assignees) => { change({ employeeIds, assignees }); setDialog(null); }} />}
    {linkWork && <ScheduleLinksDialog key={linkWork.id} schedule={schedule} work={linkWork} sections={sections}
      canEdit={canEdit} reload={reload} onClose={() => setLinkWorkId(null)} />}
    {canEdit && deleting && <ConfirmDialog open title={`Удалить работу «${deleting.name}»?`}
      description="Связи этой работы с другими работами тоже будут удалены."
      confirmLabel="Удалить работу" onOpenChange={open => { if (!open) setDeleting(null); }}
      onConfirm={() => write.mutateAsync({ kind: 'delete', id: deleting.id, version: deleting.version })} />}
  </div>;
}
