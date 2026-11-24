import { useState } from 'react';
import { useCan } from '@/shared/api/access';
import {
  CALENDAR_READ, COMPANY_CALENDAR_EDIT, useCompanyCalendar, useSaveCompanyCalendar,
  type CalendarDayKind, type CompanyCalendar,
} from '@/shared/api/planningCalendars';
import { companyToday } from '@/shared/format/companyDay';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { ChevronLeft, ChevronRight } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { MAX_YEAR, MIN_YEAR, toISO } from '@/shared/utils/date';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { CalendarWriteRefusal, DayKindField, DayLabel } from './CalendarFields';
import { CalendarMonth } from './CalendarMonth';
import { CalendarImportDialog, CompanyWeekDialog } from './CompanyCalendarDialogs';
import { DAY_LABELS, replaceDay, weekLabel, YEAR_CONFLICT } from './calendarDays';

const quarters = ['I', 'II', 'III', 'IV'] as const;
const quarterOf = (iso: string) => quarters[Math.floor((Number(iso.slice(5, 7)) - 1) / 3)];
const quarterItems = quarters.map(value => ({ value, label: `${value} квартал` }));

export function CompanyCalendarPage() {
  const can = useCan();
  const canEdit = can.permission(COMPANY_CALENDAR_EDIT);
  const [year, setYear] = useState(() => Number(companyToday().slice(0, 4)));
  const [open, setOpen] = useState<'settings' | 'import' | null>(null);
  const query = useCompanyCalendar(year, CALENDAR_READ.some(can.permission));
  const saveDay = useSaveCompanyCalendar(year);
  const calendar = query.data;

  function moveYear(offset: number) {
    saveDay.reset();
    setYear(current => current + offset);
  }

  function reload() {
    void query.refetch().then(result => {
      if (result.isSuccess) {
        saveDay.reset();
        setOpen(null);
      }
    });
  }

  return <div className="h-full overflow-auto">
    <section className="mx-auto grid min-w-0 max-w-7xl grid-cols-1 gap-4 px-4 py-5 sm:px-6">
      <PageHeader title="Производственный календарь" actions={canEdit && calendar &&
        <Button variant="filled" disabled={saveDay.isPending} onClick={() => setOpen('import')}>Загрузить календарь…</Button>} />
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <IconButton icon={ChevronLeft} label="Предыдущий год" disabled={saveDay.isPending || year <= MIN_YEAR}
            onClick={() => moveYear(-1)} />
          <span className="text-title-large" aria-live="polite">{year}</span>
          <IconButton icon={ChevronRight} label="Следующий год" disabled={saveDay.isPending || year >= MAX_YEAR}
            onClick={() => moveYear(1)} />
          {calendar && <span className="text-body-medium text-on-surface-variant">
            {weekLabel(calendar.workingWeek)} · {calendar.hoursPerShift} ч/смена
          </span>}
        </div>
        {calendar && canEdit && <Button disabled={saveDay.isPending} onClick={() => setOpen('settings')}>Рабочая неделя и часы…</Button>}
      </div>
      {query.isPending && <p role="status" className="text-body-medium">Читаем календарь…</p>}
      {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: reload }]}>
        {apiError(query.error, 'Календарь не прочитался.')}
      </Banner>}
      {calendar && <>
        {calendar.gaps.map(gap => <Banner key={gap.year} tone="warning">{gap.text}</Banner>)}
        {calendar.importFileName && <p className="text-body-medium text-on-surface-variant">
          Последняя загрузка · {calendar.importFileName}{calendar.importedAt && ` · ${formatDate(calendar.importedAt)}`}
        </p>}
        <CalendarWriteRefusal error={saveDay.error} conflict={YEAR_CONFLICT} onReload={reload} />
        <CompanyQuarter key={year} calendar={calendar} saving={saveDay.isPending} stale={isConflict(saveDay.error)}
          onSaveDay={canEdit ? (date, kind) => saveDay.mutate({ version: calendar.version, input: {
            workingWeek: calendar.workingWeek, hoursPerShift: calendar.hoursPerShift,
            days: replaceDay(calendar.days, date, kind),
          } }) : undefined} />
        {canEdit && open === 'import' && <CalendarImportDialog year={year} onClose={() => setOpen(null)} />}
        {canEdit && open === 'settings' && <CompanyWeekDialog calendar={calendar} onClose={() => setOpen(null)} onReload={reload} />}
      </>}
    </section>
  </div>;
}

function CompanyQuarter({ calendar, saving, stale, onSaveDay }: {
  calendar: CompanyCalendar; saving: boolean; stale: boolean;
  onSaveDay?: (date: string, kind: CalendarDayKind | null) => void;
}) {
  const [selected, setSelected] = useState(() => {
    const today = companyToday();
    return today.startsWith(`${calendar.year}-`) ? today : toISO(calendar.year, 1, 1);
  });
  const quarter = quarterOf(selected);
  const firstMonth = quarters.indexOf(quarter) * 3 + 1;
  const days = new Map(calendar.days.map(day => [day.date, day.kind]));
  return <div className="grid min-w-0 gap-3">
    <div aria-label="Обозначения" className="flex flex-wrap gap-2">
      {(Object.keys(DAY_LABELS) as CalendarDayKind[]).map(kind => <DayLabel key={kind} kind={kind} fallback="" />)}
    </div>
    <Tabs id="company-calendar-quarter" aria-label="Квартал" variant="segmented" value={quarter}
      items={quarterItems} onChange={value => setSelected(toISO(calendar.year, quarters.indexOf(value) * 3 + 1, 1))} />
    <TabPanel id="company-calendar-quarter" value={quarter}>
      <div className="grid min-w-0 items-start gap-3 @container">
        <div className="grid min-w-0 items-start gap-3 @4xl:grid-cols-[minmax(0,1fr)_15rem]">
          <div className="grid min-w-0 grid-cols-[repeat(auto-fit,minmax(min(100%,12rem),1fr))] gap-3">
            {[firstMonth, firstMonth + 1, firstMonth + 2].map(month => <CalendarMonth key={month} year={calendar.year}
              month={month} selected={selected} days={days} week={calendar.workingWeek} onSelect={setSelected} />)}
          </div>
          <CompanyDayEditor key={`${selected}/${calendar.version}`} selected={selected}
            kind={days.get(selected) ?? null} saving={saving} stale={stale} onSave={onSaveDay} />
        </div>
      </div>
    </TabPanel>
  </div>;
}

function CompanyDayEditor({ selected, kind, saving, stale, onSave }: {
  selected: string; kind: CalendarDayKind | null; saving: boolean; stale: boolean;
  onSave?: (date: string, kind: CalendarDayKind | null) => void;
}) {
  const [draft, setDraft] = useState(kind);
  return <Card className="min-w-0" title={formatDate(selected)}>
    <div className="flex flex-wrap items-center gap-1 text-body-medium">Сейчас · <DayLabel kind={kind} fallback="По рабочей неделе" /></div>
    {onSave && <>
      <DayKindField value={draft} baselineLabel="По рабочей неделе" onChange={setDraft} disabled={saving} />
      <Button variant="tonal" loading={saving} disabled={draft === kind || stale}
        onClick={() => onSave(selected, draft)}>Сохранить день</Button>
    </>}
  </Card>;
}
