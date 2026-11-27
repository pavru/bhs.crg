import { useEffect, useRef, useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { Icon } from '@/shared/ui/Icon';
import { Add, ArrowBack, ArrowForward, CloudDone, DarkMode, HistoryIcon, Info, Schedule, Warning } from '@/shared/ui/icons';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { LineFlow, WorkPicker, type EditorStart } from './LineFlow';
import { deliveryLabels, lifeLabels, needsLineFields, previous, shortDate, today, work, type Line, type Report, type Shift, type Variant } from './sample';

const lifeTone: Record<Report['status'], string> = {
  returned: 'text-error', draft: 'text-on-surface-variant', submitted: 'text-primary', accepted: 'text-success',
};

function LifeStatus({ status }: { status: Report['status'] }) {
  return <span className={'mob-life ' + lifeTone[status]}>{lifeLabels[status]}</span>;
}

export function DeliveryMark({ delivery }: { delivery: Report['delivery'] }) {
  const label = deliveryLabels[delivery];
  return <span className="mob-delivery" data-delivery={delivery} role="img" aria-label={label} title={label}>
    <Icon icon={delivery === 'delivered' ? CloudDone : Schedule} size={20} />
  </span>;
}

export function ShiftMeta({ report }: { report: Report }) {
  return <span className="mob-meta">
    <time dateTime={report.date}>{shortDate(report.date)}</time><span aria-hidden>·</span>
    {report.shift === 'night' && <Icon icon={DarkMode} size={18} />}
    <span>{report.shift === 'night' ? 'Ночь' : 'День'}</span>
  </span>;
}

export function ReportList({ reports, selected, onOpen, onCreate }: {
  reports: Report[]; selected: string | null; onOpen: (id: string) => void; onCreate: () => void;
}) {
  const returned = reports.filter(report => report.status === 'returned');
  const other = reports.filter(report => report.status !== 'returned').sort((a, b) => b.date.localeCompare(a.date));
  const savedNight = reports.find(report => report.date === previous && report.shift === 'night' &&
    (report.status === 'draft' || report.status === 'returned'));
  function group(label: string, items: Report[]) {
    return items.length > 0 && <section className="mob-report-group" aria-label={label}>
      <h3>{label}</h3>
      <ul>{items.map(report => <li key={report.id}>
        <Button variant="text" multiline className="mob-report-row" aria-current={report.id === selected ? 'true' : undefined}
          onClick={() => onOpen(report.id)}>
          <DeliveryMark delivery={report.delivery} />
          <span className="mob-report-row-main"><span>{report.siteName}</span><ShiftMeta report={report} /></span>
          <LifeStatus status={report.status} />
        </Button>
      </li>)}</ul>
    </section>;
  }
  return <aside className="mob-report-list" aria-label="Список моих отчётов">
    <h2>Мои отчёты</h2>
    <div className="mob-list-actions">
      {savedNight && <Button variant="tonal" icon={<Icon icon={DarkMode} size={18} />} onClick={() => onOpen(savedNight.id)}>
        Ночная смена {shortDate(savedNight.date)}
      </Button>}
      <Button variant={savedNight ? 'outlined' : 'tonal'} icon={<Icon icon={Add} size={18} />} onClick={onCreate}>Новый отчёт</Button>
    </div>
    {group('На доработке', returned)}
    {group('Другие отчёты', other)}
  </aside>;
}

const shifts: { value: Shift; label: string }[] = [{ value: 'day', label: 'День' }, { value: 'night', label: 'Ночь' }];

export function ReportView({ report, variant, offline, focusOnOpen, onBack, onEnterReport, onChange }: {
  report: Report; variant: Variant; offline: boolean; focusOnOpen: boolean;
  onBack: () => void; onEnterReport: () => void; onChange: (next: Report) => void;
}) {
  const [editor, setEditor] = useState<EditorStart>();
  const [changingShift, setChangingShift] = useState(false);
  const [alert, setAlert] = useState('');
  const [dateError, setDateError] = useState<string | null>(null);
  const title = useRef<HTMLHeadingElement>(null);
  const editable = report.status === 'draft' || report.status === 'returned';
  const sections = [...new Set(report.lines.map(line => line.section).filter(Boolean))].sort();
  const change = (next: Partial<Report>) => onChange({ ...report, ...next, delivery: 'local' });
  useEffect(() => {
    if (focusOnOpen && !editor) title.current?.focus({ preventScroll: true });
  }, [focusOnOpen, editor]);

  function beginEdit(start: EditorStart) {
    onEnterReport();
    setEditor(start);
  }

  function saveLine(line: Line) {
    const lines = report.lines.some(row => row.id === line.id) ?
      report.lines.map(row => row.id === line.id ? line : row) : [...report.lines, line];
    change({ lines });
    setEditor(undefined);
    setAlert('');
  }

  function submit() {
    const hours = Number(report.hours.replace(',', '.'));
    if (!report.hours.trim() || !Number.isFinite(hours) || hours < 0 || hours > 24) {
      setAlert('Укажите часы смены от 0 до 24.');
      return;
    }
    if (!report.lines.length) {
      setAlert('Добавьте хотя бы одну работу.');
      return;
    }
    const incomplete = report.lines.find(line => needsLineFields(line, !!report.siteId).length > 0);
    if (incomplete) {
      setAlert('Проверьте строку «' + incomplete.name + '». Не заполнены: ' + needsLineFields(incomplete, !!report.siteId).join(', ') + '.');
      return;
    }
    onChange({ ...report, status: 'submitted', delivery: offline ? 'local' : 'delivered' });
    setAlert('');
  }

  const editorFlow = editor && <LineFlow variant={variant} report={report} start={editor}
    onCancel={() => setEditor(undefined)} onSave={saveLine} />;
  if (variant === 'a' && editorFlow) return <article className="mob-report-view" aria-label="Работа отчёта">
    <header className="mob-detail-head"><ShiftMeta report={report} /><h2>{report.siteName}</h2></header>
    <div className="mob-detail-body">{editorFlow}</div>
  </article>;

  return <article className="mob-report-view" aria-label="Отчёт смены">
    <div className="mob-detail-back">
      <Button variant="text" icon={<Icon icon={ArrowBack} size={18} />} onClick={onBack}>Мои отчёты</Button>
    </div>
    <header className="mob-detail-head">
      <div className="mob-detail-meta">
        {editable ? <Button variant="text" className="mob-date-action" aria-label="Изменить дату и смену" aria-expanded={changingShift}
          onClick={() => setChangingShift(!changingShift)}><ShiftMeta report={report} /></Button> : <ShiftMeta report={report} />}
        <div className="mob-status-line"><DeliveryMark delivery={report.delivery} />
          <LifeStatus status={report.status} />
        </div>
      </div>
      <h2 ref={title} tabIndex={-1}>{report.siteName}</h2>
      {sections.length > 0 && <p className="mob-muted">{sections.join(', ')}</p>}
      {!report.siteId && <StatusText icon={Info}>Объекта нет в списке</StatusText>}
      {editable && changingShift && <div className="mob-shift-edit">
        <DateField label="Дата начала смены" value={report.date} today={today} onIncomplete={setDateError}
          onChange={date => { if (date) change({ date }); }} max={today} required />
        <SegmentedButton aria-label="Смена" items={shifts} value={report.shift} onChange={shift => change({ shift })} />
      </div>}
    </header>
    <div className="mob-detail-body">
      {report.returnNote && <ReturnNote key={report.status === 'returned' ? 'current' : 'history'} report={report} />}
      {editorFlow || <>
        {variant === 'b' && editable && <section className="mob-inline-picker">
          <h3>Добавить работу</h3>
          <WorkPicker siteId={report.siteId} onChoose={choice => beginEdit({ choice })} />
        </section>}
        <section className="mob-works" aria-label="Работы смены">
          <div className="mob-section-title"><h3>Работы</h3><span className="mob-muted">{report.lines.length || ''}</span></div>
          {report.lines.length ? <ul className="mob-work-list">{report.lines.map(line => <WorkRow key={line.id} line={line}
            editable={editable} edit={() => beginEdit({ line })} />)}</ul> : <p className="mob-muted">Добавьте первую работу смены.</p>}
          {editable && variant !== 'b' && <Button variant={variant === 'c' ? 'outlined' : 'tonal'}
            icon={<Icon icon={variant === 'c' ? ArrowForward : Add} size={18} />} onClick={() => beginEdit({})}>
            {variant === 'c' ? 'Выбрать работу' : 'Добавить работу'}
          </Button>}
        </section>
        {editable ? <div className="mob-hours">
          <TextField label="Часы смены" inputMode="decimal" value={report.hours} onChange={e => change({ hours: e.target.value })} />
        </div> : <p className="mob-hours-read"><Icon icon={Schedule} size={20} />{report.hours} ч за смену</p>}
        {alert && <Banner tone="error">{alert}</Banner>}
        {editable ? <div className="mob-actions">
          <Button variant="filled" disabled={!!dateError} onClick={submit}>{report.status === 'returned' ? 'Подать снова' : 'Подать отчёт'}</Button>
        </div> : report.status === 'submitted' && report.delivery === 'local' &&
          <StatusText icon={Schedule}>Подан на телефоне. Ожидает связи.</StatusText>}
      </>}
    </div>
  </article>;
}

function ReturnNote({ report }: { report: Report }) {
  const [open, setOpen] = useState(false);
  const returned = report.status === 'returned';
  return <section className="mob-return" data-returned={returned}>
    <div className="mob-return-heading">
      <Icon icon={returned ? Warning : HistoryIcon} size={20} />
      <strong>{returned ? 'Комментарий к возврату' : 'Был возврат 28.11'}</strong>
    </div>
    <div className="mob-return-content">
      <p className={open ? '' : 'mob-ellipsis'}>{report.returnNote}</p>
      <Button variant="text" aria-expanded={open} onClick={() => setOpen(!open)}>
        {open ? 'Свернуть' : 'Ещё'}
      </Button>
    </div>
  </section>;
}

function WorkRow({ line, editable, edit }: { line: Line; editable: boolean; edit: () => void }) {
  const item = work.find(entry => entry.id === line.workId);
  return <li className="mob-work-row">
    <div className="mob-work-title"><strong>{line.name}</strong><span>{line.quantity} {line.unit}</span></div>
    <div className="mob-work-meta"><span>{[line.section, line.variant, line.place].filter(Boolean).join(' · ')}</span>
      {editable && <Button variant="text" aria-label={'Изменить работу «' + line.name + '»'} onClick={edit}>Изменить</Button>}
    </div>
    {item?.web && <StatusText>Есть поля для веба</StatusText>}
  </li>;
}
