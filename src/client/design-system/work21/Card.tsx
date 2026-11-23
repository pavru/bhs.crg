import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { ArrowBack, Assignment, Check, Edit, Group, HistoryIcon } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { DecisionDialog, type Decision } from './DecisionDialog';
import { ReportMarks } from './Queue';
import { briefDate, sampleLines, shiftName, siteName, type Line, type Report, type Scenario, type Variant } from './model';
import cableTray from './cable-tray.webp';

const statusLabels: Record<Report['status'], { label: string; tone: StatusTone }> = {
  submitted: { label: 'Ждёт приёмки', tone: 'primary' },
  accepted: { label: 'Принят', tone: 'success' },
  returned: { label: 'Возвращён на правку', tone: 'warning' },
};

type JournalEntry = { title: string; author: string; at: string; note?: string };

function Journal({ report, edited }: { report: Report; edited: boolean }) {
  const entries: JournalEntry[] = [
    ...(report.status === 'accepted' ? [{ title: 'Отчёт принят', author: 'Елена Соколова', at: '23.11 · 10:42' }] : []),
    ...(report.status === 'returned' ? [{ title: 'Возвращён на правку', author: 'Елена Соколова', at: '23.11 · 10:42', note: report.comment }] : []),
    ...(edited ? [{ title: 'Правка строк менеджером', author: 'Елена Соколова', at: '23.11 · 10:40' }] : []),
    { title: 'Отчёт подан', author: report.employee, at: '22.11 · 08:05' },
    { title: 'Отчёт создан', author: report.enteredBy ?? report.employee, at: '21.11 · 22:10',
      note: report.enteredBy ? `Введён за сотрудника: ${report.employee}` : undefined },
  ];
  return <ol className="work21-journal">
    {entries.map(item => <li key={item.title} className="work21-journal-entry">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3">
        <p className="text-label-large">{item.title}</p>
        <span className="text-body-small text-on-surface-variant tabular-nums">{item.at}</span>
      </div>
      <p className="text-body-medium text-on-surface-variant">{item.author}</p>
      {item.note && <p className="text-body-medium mt-1">{item.note}</p>}
    </li>)}
  </ol>;
}

function VersionBar({ value, onChange, variant }: {
  value: 'current' | 'previous';
  onChange: (value: 'current' | 'previous') => void;
  variant: Variant;
}) {
  return <div className="work21-version">
    {variant === 'c' ? <Tabs id="work21-version" variant="segmented" aria-label="Версия отчёта" value={value} onChange={onChange}
      items={[{ value: 'current', label: 'Действующая' }, { value: 'previous', label: 'На момент закрытия' }]} />
      : <Select label="Версия отчёта" density="dense" value={value} onValueChange={next => onChange(next === 'previous' ? 'previous' : 'current')}>
        <SelectItem value="current">Версия 2 · действующая</SelectItem>
        <SelectItem value="previous">Версия 1 · на момент закрытия</SelectItem>
      </Select>}
    <div className="text-body-medium">
      {value === 'previous'
        ? <><p className="font-medium">Только чтение</p><p className="text-on-surface-variant">Елена Соколова · 23.11.2026 · Уточнён объём прокладки по обмеру</p></>
        : <p className="text-on-surface-variant">Исправлен после закрытия периода · 23.11.2026</p>}
    </div>
  </div>;
}

function Lines({ lines, onPhoto }: { lines: Line[]; onPhoto: (line: Line) => void }) {
  const columns: DataGridColumn<Line>[] = [
    { key: 'name', label: 'Работа и место', rowHeader: true, wrap: true, cell: row => <>
      <span className="font-medium">{row.name}</span>
      <span className="block text-body-small text-on-surface-variant">{row.place}</span>
    </> },
    { key: 'volume', label: 'Объём', align: 'right', cell: row => `${row.volume} ${row.unit}` },
    { key: 'photos', label: 'Фото', wrap: true, interactive: true, cell: row => <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
      {row.photos > 0 && <button type="button" className="work21-photo focus-ring" aria-label={`Открыть фото: ${row.name}`} onClick={() => onPhoto(row)}>
        <img src={cableTray} alt="" />
      </button>}
      {row.pending > 0 && <StatusText tone="neutral">фото не загружены: {row.pending}</StatusText>}
      {row.photos === 0 && row.pending === 0 && <span className="text-on-surface-variant">—</span>}
    </div> },
  ];
  return <DataGrid columns={columns} rows={lines} rowKey={line => line.id} density="compact" />;
}

function EditLines({ rows, onChange }: { rows: Line[]; onChange: (lines: Line[]) => void }) {
  const columns: EditGridColumn<Line>[] = [
    { key: 'name', label: 'Работа', rowHeader: true, cell: row => row.name },
    { key: 'volume', label: 'Объём', width: 'w-32', cell: (row, i) => <TextField density="cell" label={`Объём строки ${i + 1}`}
      value={row.volume} inputMode="decimal" onChange={event => onChange(rows.map(line => line.id === row.id ? { ...line, volume: event.target.value } : line))} /> },
    { key: 'unit', label: 'Ед.', cell: row => row.unit },
    { key: 'place', label: 'Место', cell: (row, i) => <TextField density="cell" label={`Место строки ${i + 1}`}
      value={row.place} onChange={event => onChange(rows.map(line => line.id === row.id ? { ...line, place: event.target.value } : line))} /> },
  ];
  return <EditGrid columns={columns} rows={rows} rowKey={row => row.id} label="Правка строк отчёта" numbered sheet />;
}

export function Card({ report, scenario, variant, embedded, onBack, onCrew, onUpdate }: {
  report: Report;
  scenario: Scenario;
  variant: Variant;
  embedded: boolean;
  onBack: () => void;
  onCrew: () => void;
  onUpdate: (report: Report) => void;
}) {
  const [decision, setDecision] = useState<Decision | null>(null);
  const [version, setVersion] = useState<'current' | 'previous'>('current');
  const [section, setSection] = useState<'lines' | 'journal'>('lines');
  const savedLines = report.workLines ?? sampleLines(report);
  const [edit, setEdit] = useState<Line[] | null>(null);
  const edited = report.marks.some(mark => mark.code === 'manager-edit');
  const [photo, setPhoto] = useState<Line | null>(null);
  const [refreshed, setRefreshed] = useState(false);
  const historical = scenario === 'history' && version === 'previous';
  const editable = !historical && scenario !== 'reader' && scenario !== 'history' && report.actions.includes('edit');
  const canAccept = !historical && scenario !== 'reader' && scenario !== 'history' && report.actions.includes('accept');
  const canReturn = !historical && scenario !== 'reader' && scenario !== 'history' && report.actions.includes('return');
  const status = scenario === 'history' ? statusLabels.accepted : statusLabels[report.status];
  const compact = variant === 'b';
  const lines = historical ? savedLines.map((line, i) => i === 0 ? { ...line, volume: '42', pending: 1, photos: 0 } : line) : savedLines;

  function recordDecision(next: Decision, comment?: string) {
    onUpdate({ ...report, status: next === 'accept' ? 'accepted' : 'returned', comment, actions: [], acceptWouldMark: false });
    setDecision(null);
  }

  function reload() {
    if (scenario === 'decided') recordDecision('accept');
    else {
      setRefreshed(true);
      onUpdate({ ...report, workLines: savedLines.map((line, i) => i === 0 ? { ...line, volume: '50' } : line) });
      setDecision(null);
    }
  }

  if (scenario === 'not-found') return <EmptyState icon={<Icon icon={Assignment} size={24} />}
    title="Отчёт не найден: его нет, или он вне ваших строек"
    action={<Button variant="tonal" onClick={onBack}>К очереди</Button>} />;

  const back = <IconButton icon={ArrowBack} label="К очереди" onClick={onBack} />;

  if (scenario === 'draft') return <div className="space-y-3">
    <PageHeader icon={back} title={report.employee} subtitle={`${siteName(report.site)} · ${briefDate(report.date)} · ${shiftName[report.shift]}`} />
    <Banner tone="info">Черновик. Автоподача 23.11 в 23:59 по времени стройки.</Banner>
  </div>;

  const actionMenu = editable && <RowActionsMenu ariaLabel="Действия отчёта"
    actions={[{ key: 'edit', label: 'Править строки', icon: Edit, onSelect: () => { setSection('lines'); setEdit(savedLines); } }]} />;
  const actionButtons = edit ? <>
    <Button variant="text" onClick={() => setEdit(null)}>Отмена</Button>
    <Button variant="filled" disabled={edit.some(line => !line.volume.trim() || !Number.isFinite(Number(line.volume)))}
      onClick={() => {
        onUpdate({ ...report, workLines: edit,
          marks: edited ? report.marks : [...report.marks, { code: 'manager-edit', text: 'правка менеджером' }] });
        setEdit(null);
      }}>Сохранить</Button>
  </> : <>
    {canReturn && <Button variant="outlined" onClick={() => setDecision('return')}>Вернуть</Button>}
    {canAccept && <Button variant="filled" icon={<Icon icon={Check} size={18} />} onClick={() => setDecision('accept')}>Принять</Button>}
    {actionMenu}
  </>;
  const linePanel = <div className="min-w-0 space-y-3">
    <h2 className="text-title-medium">{edit ? 'Правка строк' : 'Выполненные работы'}</h2>
    {edit ? <EditLines rows={edit} onChange={setEdit} /> : <Lines lines={lines} onPhoto={setPhoto} />}
  </div>;
  const details = compact ? <>
    <Tabs id="work21-report" aria-label="Разделы отчёта" value={section} onChange={setSection}
      items={[{ value: 'lines', label: 'Работы' }, { value: 'journal', label: 'Журнал', icon: HistoryIcon }]} />
    <TabPanel id="work21-report" value={section}>
      {section === 'lines' ? linePanel : <Journal report={report} edited={edited} />}
    </TabPanel>
  </> : <div className={variant === 'c' ? 'space-y-6' : 'work21-detail-columns'}>
    {linePanel}
    <section className="min-w-0 space-y-3">
      <h2 className="text-title-medium">Журнал</h2>
      <Journal report={report} edited={edited} />
    </section>
  </div>;

  return <article className={embedded ? 'work21-card work21-card-inset' : 'work21-card'}>
    <PageHeader icon={embedded ? undefined : back} title={report.employee}
      subtitle={`${siteName(report.site)} · ${briefDate(report.date)} · ${shiftName[report.shift]} · ${report.hours} ч`}
      actions={(edit || canAccept || canReturn || editable) && <div className="flex flex-col items-start gap-1 @2xl:items-end">
        <div className="flex flex-wrap items-center gap-2">{actionButtons}</div>
        {report.acceptWouldMark && canAccept && !edit && <StatusText tone="warning">При приёмке появится пометка «ввёл и принял один человек»</StatusText>}
      </div>} />
    <div className="space-y-2">
      <StatusText tone={status.tone}>{status.label}</StatusText>
      <ReportMarks report={report} />
    </div>
    {compact && report.marks.some(mark => mark.code === 'crew-mismatch') && !edit && <Banner tone="warning" icon={Group}
      actions={[{ label: 'Сверка бригад', onClick: onCrew }]}>Расходится объём по бригаде этой смены.</Banner>}
    {scenario === 'history' && <VersionBar variant={variant} value={version} onChange={setVersion} />}
    {scenario === 'history' && variant === 'c' ? <TabPanel id="work21-version" value={version}>{details}</TabPanel> : details}
    {decision && <DecisionDialog decision={decision} report={report} scenario={refreshed ? 'review' : scenario}
      onClose={() => setDecision(null)} onDecide={recordDecision} onReload={reload} />}
    {photo && <Modal open wide nothingToLose title={photo.name} onOpenChange={open => { if (!open) setPhoto(null); }}>
      <img src={cableTray} alt="Силовые кабели в лотке под потолком" className="work21-photo-view" />
      <p className="mt-2 text-body-medium text-on-surface-variant">{photo.place} · фото 1 из 1</p>
    </Modal>}
  </article>;
}
