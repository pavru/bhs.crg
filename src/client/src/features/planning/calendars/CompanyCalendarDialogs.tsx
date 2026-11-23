import { useState } from 'react';
import {
  usePreviewCalendarImport, useSaveCompanyCalendar, type CalendarImportChange, type CompanyCalendar,
} from '@/shared/api/planningCalendars';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { FileDropZone } from '@/shared/ui/FileDropZone';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { CalendarWriteRefusal, DayLabel, WeekFields } from './CalendarFields';
import { YEAR_CONFLICT } from './calendarDays';

export function CompanyWeekDialog({ calendar, onClose, onReload }: {
  calendar: CompanyCalendar; onClose: () => void; onReload: () => void;
}) {
  const [opened] = useState(calendar);
  const [week, setWeek] = useState(opened.workingWeek);
  const [hours, setHours] = useState(String(opened.hoursPerShift));
  const save = useSaveCompanyCalendar(opened.year);
  const duration = Number(hours);
  const valid = /^\d+(\.\d{1,2})?$/.test(hours) && duration > 0 && duration <= 24;
  const dirty = week !== opened.workingWeek || duration !== opened.hoursPerShift;
  return <Modal open title="Рабочая неделя компании" isDirty={dirty} busy={save.isPending}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit /><ModalPrimary disabled={!valid || (!dirty && opened.configured) || isConflict(save.error)}
      onClick={() => save.mutate({ version: opened.version, input: {
        workingWeek: week, hoursPerShift: duration, days: opened.days,
      } }, { onSuccess: onClose })}>Сохранить</ModalPrimary></ModalButtons>}>
    <div className="grid gap-4">
      <CalendarWriteRefusal error={save.error} conflict={YEAR_CONFLICT} onReload={onReload} />
      <WeekFields value={week} onChange={setWeek} disabled={save.isPending} />
      <TextField label="Часов в смене" type="number" step="0.01" min="0.01" max="24" required disabled={save.isPending}
        value={hours} onChange={event => setHours(event.currentTarget.value)} />
    </div>
  </Modal>;
}

const MAX_IMPORT_BYTES = 128 * 1024;
const changeColumns: DataGridColumn<CalendarImportChange>[] = [
  { key: 'date', label: 'Дата', rowHeader: true, cell: row => formatDate(row.date) },
  { key: 'before', label: 'Сейчас', cell: row => <DayLabel kind={row.previousKind} fallback={row.wasWorking ? 'Рабочий день' : 'Выходной'} /> },
  { key: 'after', label: 'После импорта', cell: row => <DayLabel kind={row.kind} fallback={row.isWorking ? 'Рабочий день' : 'Выходной'} /> },
];

export function CalendarImportDialog({ year, onClose }: { year: number; onClose: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const parse = usePreviewCalendarImport(year);
  const save = useSaveCompanyCalendar(year);
  const preview = parse.data;
  const isBusy = parse.isPending || save.isPending;

  function loadPreview() {
    if (!file) return;
    save.reset();
    parse.mutate(file);
  }

  function apply() {
    if (!preview) return;
    const { version, workingWeek, hoursPerShift, days, importFileName } = preview;
    save.mutate({ version, input: { workingWeek, hoursPerShift, days, importFileName } }, { onSuccess: onClose });
  }

  return <Modal open wide title={`Импорт календаря на ${year} год`} busy={isBusy} isDirty={file !== null}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit />
      {preview ? <ModalPrimary disabled={isConflict(save.error)} onClick={apply}>Применить изменения</ModalPrimary> :
        <ModalPrimary disabled={!file} onClick={loadPreview}>Посмотреть отличия</ModalPrimary>}
    </ModalButtons>}>
    <div className="grid min-w-0 gap-4">
      <CalendarWriteRefusal error={save.error} conflict="Год тем временем изменили. Перечитайте календарь и сравните изменения снова."
        onReload={loadPreview} />
      {parse.isError && <Banner tone="error">{apiError(parse.error, 'Не удалось прочитать файл календаря.')}</Banner>}
      {!preview ? <>
        <FileDropZone label="Файл календаря" accept=".ics" hint="iCalendar PPT.ru · до 128 КБ" file={file}
          onFile={next => { setFile(next); parse.reset(); }}
          check={next => {
            if (!next.name.toLowerCase().endsWith('.ics')) return 'выберите файл .ics';
            if (!next.size || next.size > MAX_IMPORT_BYTES) return 'размер файла должен быть больше нуля и до 128 КБ';
            return null;
          }} busy={parse.isPending ? 'Читаем файл…' : undefined} />
        <p className="text-body-medium text-on-surface-variant">
          Источник файла — <a href="https://ppt.ru/ipb/procalendar" target="_blank" rel="noreferrer" className="text-primary underline">PPT.ru</a>.
        </p>
      </> : <>
        <p className="text-body-medium">{preview.importFileName} · дней с изменениями: {preview.changes.length}</p>
        {preview.changes.length > 0 ? <DataGrid rows={preview.changes} columns={changeColumns} rowKey={row => row.date} /> :
          <p className="text-body-medium">Исключения совпадают с текущим календарём.</p>}
      </>}
    </div>
  </Modal>;
}
