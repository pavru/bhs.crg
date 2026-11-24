import { useState } from 'react';
import { useCan } from '@/shared/api/access';
import {
  CALENDAR_READ, SCHEDULE_EDIT, useConstructionCalendar, useSaveConstructionCalendar,
  type CalendarDay, type CalendarDayKind, type ConstructionCalendar, type ConstructionCalendarInput,
} from '@/shared/api/planningCalendars';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { CalendarWriteRefusal, DayKindField, DayLabel, WeekFields } from './CalendarFields';
import { FIVE_DAYS, replaceDay, weekLabel } from './calendarDays';

const SITE_CONFLICT = 'Календарь стройки тем временем изменили. Перечитайте его.';
const exceptionColumns: DataGridColumn<CalendarDay>[] = [
  { key: 'date', label: 'Дата', rowHeader: true, cell: day => formatDate(day.date) },
  { key: 'kind', label: 'Вид дня', cell: day => <DayLabel kind={day.kind} fallback="" /> },
];
const inputOf = ({ workingWeek, shifts, days }: ConstructionCalendar): ConstructionCalendarInput => ({ workingWeek, shifts, days });

export function ConstructionCalendarPanel({ constructionId }: { constructionId: string }) {
  const can = useCan();
  const query = useConstructionCalendar(constructionId, CALENDAR_READ.some(can.permission));
  return <section className="grid min-w-0 gap-4">
    {query.isPending && <p role="status" className="text-body-medium">Читаем календарь стройки…</p>}
    {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => { void query.refetch(); } }]}>
      {apiError(query.error, 'Календарь стройки не прочитался.')}
    </Banner>}
    {query.data && <ConstructionCalendarForm key={constructionId} calendar={query.data}
      canEdit={can.permission(SCHEDULE_EDIT)} reload={query.refetch} />}
  </section>;
}

function ConstructionCalendarForm({ calendar, canEdit, reload }: {
  calendar: ConstructionCalendar; canEdit: boolean;
  reload: () => Promise<{ isSuccess: boolean; data?: ConstructionCalendar }>;
}) {
  const [baseline, setBaseline] = useState(calendar);
  const [draft, setDraft] = useState(() => inputOf(calendar));
  const [editingDate, setEditingDate] = useState<string | null>(null);
  const save = useSaveConstructionCalendar(calendar.constructionId);
  const editingDisabled = save.isPending;
  const dirty = draft.workingWeek !== baseline.workingWeek || draft.shifts !== baseline.shifts ||
    JSON.stringify(draft.days) !== JSON.stringify(baseline.days);
  const stale = isConflict(save.error);

  // Обновление фоном меняет только чистую форму. У черновика остаётся версия, с которой его начали.
  if (calendar.version !== baseline.version && !dirty && !editingDisabled && editingDate === null) {
    setBaseline(calendar);
    setDraft(inputOf(calendar));
  }

  async function saveCalendar() {
    try {
      const saved = await save.mutateAsync({ version: baseline.version, input: draft });
      setBaseline(saved);
      setDraft(inputOf(saved));
      return true;
    } catch {
      return false;
    }
  }

  async function reloadCalendar() {
    const result = await reload();
    if (result.isSuccess && result.data) {
      setBaseline(result.data);
      setDraft(inputOf(result.data));
      save.reset();
    }
  }

  function updateDay(date: string, kind: CalendarDayKind | null) {
    setDraft(current => ({ ...current, days: replaceDay(current.days, date, kind) }));
    setEditingDate(null);
  }

  return <>
    <LeaveGuard active={canEdit && dirty} saving={editingDisabled} save={saveCalendar}
      message="Сохранить изменения календаря стройки перед переходом?"
      saveBlocked={stale ? SITE_CONFLICT : undefined} />
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h2 className="text-title-large">Календарь стройки</h2>
      {canEdit && <Button variant="filled" loading={save.isPending} disabled={!dirty || stale}
        onClick={() => { void saveCalendar(); }}>Сохранить календарь</Button>}
    </div>
    <CalendarWriteRefusal error={save.error} conflict={SITE_CONFLICT}
      onReload={() => { void reloadCalendar(); }} />
    <div className="@container min-w-0">
      <div className="grid min-w-0 items-start gap-3 @3xl:grid-cols-2">
        <Card className="min-w-0" title="Рабочая неделя и смены">
          {canEdit ? <>
            <Select label="Режим рабочей недели" value={draft.workingWeek === null ? 'company' : 'site'} disabled={editingDisabled}
              onValueChange={value => setDraft(current => ({
                ...current, workingWeek: value === 'company' ? null : FIVE_DAYS,
              }))}>
              <SelectItem value="company">Как у компании</SelectItem>
              <SelectItem value="site">Своя рабочая неделя</SelectItem>
            </Select>
            {draft.workingWeek !== null && <WeekFields value={draft.workingWeek} disabled={editingDisabled}
              onChange={value => setDraft(current => ({ ...current, workingWeek: value }))} />}
            <Select label="Смен в день" value={String(draft.shifts)} disabled={editingDisabled}
              onValueChange={value => setDraft(current => ({ ...current, shifts: Number(value) }))}>
              {[1, 2, 3].map(shifts => <SelectItem key={shifts} value={String(shifts)}>{shifts}</SelectItem>)}
            </Select>
          </> : <dl className="grid gap-2 text-body-medium">
            <div><dt className="text-on-surface-variant">Рабочая неделя</dt>
              <dd>{draft.workingWeek === null ? 'Как у компании' : weekLabel(draft.workingWeek)}</dd></div>
            <div><dt className="text-on-surface-variant">Смен в день</dt><dd>{draft.shifts}</dd></div>
          </dl>}
          <p className="text-body-medium text-on-surface-variant">Сменность учитывается в подсказке длительности.</p>
        </Card>
        <Card className="min-w-0" title="Исключения стройки">
          <p className="text-body-medium text-on-surface-variant">
            Праздники компании действуют, пока стройка явно не переопределит дату.
          </p>
          {draft.days.length > 0 ? <DataGrid rows={draft.days} columns={exceptionColumns} rowKey={day => day.date}
            onRowOpen={canEdit && !editingDisabled ? day => setEditingDate(day.date) : undefined} /> :
            <p className="text-body-medium">Своих исключений пока нет.</p>}
          {canEdit && <div><Button variant="tonal" disabled={editingDisabled}
            onClick={() => setEditingDate('')}>Добавить исключение…</Button></div>}
        </Card>
      </div>
    </div>
    {canEdit && editingDate !== null && <ConstructionDayDialog initialDate={editingDate} days={draft.days}
      onClose={() => setEditingDate(null)} onSave={updateDay} />}
  </>;
}

function ConstructionDayDialog({ initialDate, days, onClose, onSave }: {
  initialDate: string; days: CalendarDay[]; onClose: () => void; onSave: (date: string, value: CalendarDayKind | null) => void;
}) {
  const previousKind = days.find(day => day.date === initialDate)?.kind ?? null;
  const [kind, setKind] = useState<CalendarDayKind | null>(previousKind);
  const [date, setDate] = useState(initialDate);
  const [incomplete, setIncomplete] = useState(false);
  const existing = !initialDate && days.some(day => day.date === date);
  const title = initialDate ? formatDate(initialDate) : 'Добавить исключение стройки';
  return <Modal open title={title} isDirty={kind !== previousKind || date !== initialDate}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit /><ModalPrimary disabled={!date || incomplete || existing || kind === previousKind}
      onClick={() => onSave(date, kind)}>Готово</ModalPrimary></ModalButtons>}>
    <div className="grid gap-3">
      {!initialDate && <DateField label="Дата исключения" value={date} onChange={setDate} required
        onIncomplete={message => setIncomplete(Boolean(message))} />}
      {existing && <Banner tone="warning">На эту дату уже есть исключение. Его можно изменить в списке.</Banner>}
      <DayKindField value={kind} onChange={setKind} baselineLabel="По календарю без исключения стройки" />
    </div>
  </Modal>;
}
