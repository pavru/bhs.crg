import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { FileDropZone } from '@/shared/ui/FileDropZone';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { monthMatrix } from '@/shared/utils/date';
import { dateLabel, imported, labels, months, weekdays, workdays, type Days, type Kind } from './sample';

export function DayStatus({ kind }: { kind?: Kind }) {
  return kind ? <span className={`plan-calendar-status plan-calendar-${kind}`}>{labels[kind]}</span> : 'По рабочей неделе';
}

export function Months({ year, indices, days, week, selected, onDay, compact = false }: {
  year: number; indices: number[]; days: Days; week: number; selected?: string; onDay?: (day: string) => void; compact?: boolean;
}) {
  const layout = compact ? ' plan-calendar-mini' : indices.length === 12 ? ' plan-calendar-year' : '';
  return <div className={`plan-calendar-months${layout}`}>
    {indices.map(index => <section key={index} className="plan-calendar-month" aria-label={months[index]}>
      <h3>{months[index]}</h3>
      <div className="plan-calendar-days">
        {weekdays.map(day => <span key={day} className="plan-calendar-weekday">{day}</span>)}
        {monthMatrix(year, index + 1, 1).flat().map((day, cell) => {
          if (day === null) return <span aria-hidden="true" key={cell} />;
          const date = `${String(index + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
          const kind = days[date];
          const weekend = !(week & (1 << (cell % 7)));
          const mark = kind ? `plan-calendar-${kind}` : weekend ? 'plan-calendar-weekend' : '';
          const label = `${dateLabel(date)} · ${kind ? labels[kind] : weekend ? 'Выходной' : 'Рабочий день'}`;
          const content = <>{day}{kind && <span className="plan-calendar-mark" aria-hidden="true">
            {kind === 'holiday' ? '×' : kind === 'short' ? '−' : '+'}
          </span>}</>;
          return onDay ? <button key={cell} type="button" className={`focus-ring plan-calendar-cell ${mark}`}
            aria-label={label} aria-pressed={date === selected} onClick={() => onDay(date)}>{content}</button>
            : <span key={cell} aria-label={label} className={`plan-calendar-cell ${mark}`}>{content}</span>;
        })}
      </div>
    </section>)}
  </div>;
}

export function WeekFields({ week, onChange, readOnly = false }: {
  week: number; onChange: (week: number) => void; readOnly?: boolean;
}) {
  return <fieldset className="grid gap-2">
    <legend className="text-label-large">Рабочие дни недели</legend>
    <div className="flex flex-wrap gap-x-5 gap-y-2">{weekdays.map((label, index) => {
      const mask = 1 << index;
      const checked = Boolean(week & mask);
      return <Checkbox key={label} label={label} aria-label={workdays[index]} checked={checked}
        disabled={readOnly || week === mask} onChange={value => onChange(value ? week | mask : week & ~mask)} />;
    })}</div>
  </fieldset>;
}

export function Exceptions({ days, onDay }: { days: Days; onDay?: (day: string) => void }) {
  const rows = Object.entries(days).sort(([a], [b]) => a.localeCompare(b)).map(([date, kind]) => ({ date, kind }));
  const columns: DataGridColumn<typeof rows[number]>[] = [
    { key: 'date', label: 'Дата', rowHeader: true, cell: row => dateLabel(row.date) },
    { key: 'kind', label: 'Вид дня', cell: row => <DayStatus kind={row.kind} /> },
  ];
  return rows.length ? <DataGrid rows={rows} rowKey={row => row.date} columns={columns}
    onRowOpen={onDay && (row => onDay(row.date))} /> : <p className="text-body-medium text-on-surface-variant">Исключений нет.</p>;
}

export function DayEditor({ seedDate, year, days, readOnly, onSave, inline = false }: {
  seedDate: string; year: number; days: Days; readOnly: boolean; onSave: (date: string, kind: Kind | undefined) => void; inline?: boolean;
}) {
  const [date, setDate] = useState(seedDate ? `${year}-${seedDate}` : '');
  const [kind, setKind] = useState<Kind | 'default'>(days[seedDate] ?? 'default');
  const [incomplete, setIncomplete] = useState(false);
  return <div className="grid gap-3">
    {readOnly ? <p className="text-body-medium">Вид дня · <DayStatus kind={days[seedDate]} /></p> : <>
      {seedDate ? <p className="text-body-medium text-on-surface-variant">Сейчас · <DayStatus kind={days[seedDate]} /></p> :
        <DateField label="Дата" min={`${year}-01-01`} max={`${year}-12-31`} value={date} required
          onChange={setDate} onIncomplete={message => setIncomplete(Boolean(message))} />}
      <Select label="Вид дня" value={kind} onValueChange={value => setKind(value as Kind | 'default')}>
        <SelectItem value="default">По рабочей неделе</SelectItem>
        {Object.entries(labels).map(([value, text]) => <SelectItem key={value} value={value}>{text}</SelectItem>)}
      </Select>
      {inline ? <Button variant="tonal" disabled={!date || incomplete}
        onClick={() => onSave(date.slice(5), kind === 'default' ? undefined : kind)}>Сохранить день</Button>
        : <ModalPrimary disabled={!date || incomplete} onClick={() => onSave(date.slice(5), kind === 'default' ? undefined : kind)}>Сохранить день</ModalPrimary>}
    </>}
  </div>;
}

export function ImportPreview({ year, days, canSave, conflict, onClose, onApply }: {
  year: number; days: Days; canSave: boolean; conflict: boolean; onClose: () => void; onApply: () => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState(false);
  const [stale, setStale] = useState(false);
  const allDates = [...new Set([...Object.keys(days), ...Object.keys(imported)])].sort();
  const rows = allDates.filter(date => days[date] !== imported[date]).map(date => ({ date, before: days[date], after: imported[date] }));
  const columns: DataGridColumn<typeof rows[number]>[] = [
    { key: 'date', label: 'Дата', rowHeader: true, cell: row => dateLabel(row.date) },
    { key: 'before', label: 'Сейчас', cell: row => <DayStatus kind={row.before} /> },
    { key: 'after', label: 'После импорта', cell: row => <DayStatus kind={row.after} /> },
  ];
  return <Modal open wide title={`Импорт календаря на ${year} год`} isDirty={Boolean(file || preview)}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit />
      {preview
        ? <ModalPrimary disabled={!canSave || stale} onClick={() => conflict ? setStale(true) : onApply()}>Применить изменения</ModalPrimary>
        : <ModalPrimary disabled={!file} onClick={() => setPreview(true)}>Посмотреть отличия</ModalPrimary>}
    </ModalButtons>}>
    <div className="grid gap-3">
      {stale && <Banner tone="warning" actions={[{ label: 'Перечитать', onClick: () => { setStale(false); setPreview(false); } }]}>
        Год тем временем изменили. Перечитайте календарь и сравните изменения снова.
      </Banner>}
      {!preview ? <>
        <FileDropZone label="Файл календаря" accept=".ics" hint="Выгрузка PPT.ru · .ics" file={file} onFile={setFile}
          check={value => value.name.toLowerCase().endsWith('.ics') ? null : 'выберите файл .ics'} />
        <Button onClick={() => setPreview(true)}>Открыть пример предпросмотра</Button>
      </> : <>
        <p className="text-body-medium">{file?.name ?? `${year}.ics · пример`} · дней с изменениями: {rows.length}</p>
        <DataGrid rows={rows} columns={columns} rowKey={row => row.date} />
      </>}
    </div>
  </Modal>;
}
