import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { ChevronLeft, ChevronRight } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tabs } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { DayEditor, DayStatus, Exceptions, ImportPreview, Months, WeekFields } from './CalendarParts';
import { dateLabel, example, imported, labels, scenarios, siteExceptions, variants, weekdays,
  type Days, type Kind, type Scenario, type Variant } from './sample';

const fullYear = Array.from({ length: 12 }, (_, index) => index);
const quarters = ['I', 'II', 'III', 'IV'] as const;
type Quarter = (typeof quarters)[number];
const areas = [{ value: 'company', label: 'Календарь компании' }, { value: 'site', label: 'График стройки' }] as const;
type Area = (typeof areas)[number]['value'];
type WorkWeek = { week: number; hours: string };
type SavedYear = WorkWeek & { days: Days };
const EXAMPLE_YEAR = 2027;
const EMPTY_YEAR: SavedYear = { days: {}, week: 31, hours: '8' };

function Company({ variant, scenario }: { variant: Variant; scenario: Scenario }) {
  const readOnly = scenario === 'readonly';
  const [year, setYear] = useState(EXAMPLE_YEAR);
  const [years, setYears] = useState<Partial<Record<number, SavedYear>>>(() =>
    scenario === 'empty' ? {} : { [EXAMPLE_YEAR]: { ...EMPTY_YEAR, days: example } });
  const [quarter, setQuarter] = useState<Quarter>('I');
  const [editor, setEditor] = useState<string | null>(null);
  const [selected, setSelected] = useState('02-20');
  const [weekDraft, setWeekDraft] = useState<WorkWeek | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [stale, setStale] = useState(false);
  const [notice, setNotice] = useState('');
  const { days: visibleDays, week, hours } = years[year] ?? EMPTY_YEAR;
  const start = 3 * quarters.indexOf(quarter);

  function updateYear(patch: Partial<SavedYear>) {
    setYears(previous => ({ ...previous, [year]: { ...(previous[year] ?? EMPTY_YEAR), ...patch } }));
  }

  function changeYear(offset: number) {
    setYear(value => value + offset);
    setSelected(`${String(start + 1).padStart(2, '0')}-01`);
    setStale(false);
    setNotice('');
  }

  function choose(date: string) {
    setSelected(date);
    if (variant !== 'quarter') setEditor(date);
  }

  function save(date: string, kind?: Kind) {
    if (scenario === 'conflict') { setStale(true); return; }
    const days = { ...visibleDays };
    if (kind) days[date] = kind;
    else delete days[date];
    updateYear({ days });
    setEditor(null);
    setNotice(`День ${dateLabel(date)} сохранён.`);
  }

  return <div className="grid min-w-0 gap-4">
    <PageHeader title="Производственный календарь" actions={!readOnly &&
      <Button variant="filled" onClick={() => setImportOpen(true)}>Загрузить календарь…</Button>} />
    <div className="flex flex-wrap items-center justify-between gap-2">
      <div className="flex items-center gap-2">
        <IconButton icon={ChevronLeft} label="Предыдущий год" onClick={() => changeYear(-1)} />
        <strong className="text-title-large">{year}</strong>
        <IconButton icon={ChevronRight} label="Следующий год" onClick={() => changeYear(1)} />
        <span className="text-body-medium text-on-surface-variant">{weekdays.filter((_, i) => week & (1 << i)).join(' · ')} · {hours} ч/смена</span>
      </div>
      {!readOnly && <Button onClick={() => setWeekDraft({ week, hours })}>Рабочая неделя и часы…</Button>}
    </div>
    {!years[year] && <Banner tone="warning">Календарь компании на {year} год не заполнен.</Banner>}
    {stale && <Banner tone="warning" actions={[{ label: 'Перечитать', onClick: () => setStale(false) }]}>
      Год тем временем изменили. Перечитайте календарь.
    </Banner>}
    {notice && <p role="status" className="text-body-medium text-on-surface-variant">{notice}</p>}
    <div className="flex flex-wrap gap-2" aria-label="Обозначения">
      {(Object.keys(labels) as Kind[]).map(kind => <DayStatus key={kind} kind={kind} />)}
    </div>
    {variant === 'year' && <Months year={year} indices={fullYear} days={visibleDays} week={week} onDay={readOnly ? undefined : choose} />}
    {variant === 'quarter' && <>
      <Tabs id="plan-calendar-quarters" aria-label="Квартал" variant="segmented" value={quarter}
        items={quarters.map(value => ({ value, label: `${value} квартал` }))} onChange={setQuarter} />
      <div className="plan-calendar-quarter">
        <Months year={year} indices={fullYear.slice(start, start + 3)} days={visibleDays} week={week} selected={selected} onDay={choose} />
        <aside className="plan-calendar-card">
          <h2 className="text-title-medium">{dateLabel(selected)} {year}</h2>
          <DayEditor key={`${year}/${selected}`} seedDate={selected} year={year} days={visibleDays} readOnly={readOnly} onSave={save} inline />
        </aside>
      </div>
    </>}
    {variant === 'exceptions' && <div className="plan-calendar-exceptions">
      <section className="plan-calendar-card">
        <h2 className="text-title-medium">Исключения года</h2>
        {!readOnly && <div><Button variant="tonal" onClick={() => setEditor('')}>Добавить исключение…</Button></div>}
        <Exceptions days={visibleDays} onDay={readOnly ? undefined : choose} />
      </section>
      <Months year={year} indices={fullYear} days={visibleDays} week={week} compact onDay={readOnly ? undefined : choose} />
    </div>}
    {editor !== null && <Modal open title={editor ? `${dateLabel(editor)} ${year}` : 'Добавить исключение'}
      onOpenChange={open => { if (!open) setEditor(null); }}>
      {stale && <Banner tone="warning">Год тем временем изменили. Закройте окно и перечитайте календарь.</Banner>}
      <DayEditor seedDate={editor} year={year} days={visibleDays} readOnly={false} onSave={save} inline />
    </Modal>}
    {importOpen && <ImportPreview year={year} days={visibleDays} canSave={!readOnly} conflict={scenario === 'conflict'}
      onClose={() => setImportOpen(false)} onApply={() => {
        updateYear({ days: imported });
        setImportOpen(false);
        setNotice('Календарь загружен.');
      }} />}
    {weekDraft && <Modal open title="Рабочая неделя компании" onOpenChange={open => { if (!open) setWeekDraft(null); }}>
      <div className="grid gap-3">
        <WeekFields week={weekDraft.week} onChange={value => setWeekDraft({ ...weekDraft, week: value })} />
        <TextField label="Часов в смене" type="number" min="0.01" max="24" value={weekDraft.hours}
          onChange={event => setWeekDraft({ ...weekDraft, hours: event.currentTarget.value })} />
        <Button variant="tonal" onClick={() => { updateYear(weekDraft); setWeekDraft(null); }}>Применить</Button>
      </div>
    </Modal>}
  </div>;
}

function Site({ variant, scenario }: { variant: Variant; scenario: Scenario }) {
  const readOnly = scenario === 'readonly';
  const [inherit, setInherit] = useState(false);
  const [week, setWeek] = useState(63);
  const [shifts, setShifts] = useState('2');
  const [days, setDays] = useState<Days>(siteExceptions);
  const [editor, setEditor] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const [view, setView] = useState<'week' | 'days'>('week');
  const settings = <section className="plan-calendar-card">
    <h3 className="text-title-medium">Рабочая неделя и смены</h3>
    <Select label="Режим рабочей недели" disabled={readOnly} value={inherit ? 'company' : 'site'}
      onValueChange={value => setInherit(value === 'company')}>
      <SelectItem value="company">Как у компании</SelectItem><SelectItem value="site">Своя рабочая неделя</SelectItem>
    </Select>
    <WeekFields week={inherit ? 31 : week} onChange={setWeek} readOnly={inherit || readOnly} />
    <Select label="Смен в день" disabled={readOnly} value={shifts} onValueChange={setShifts}>
      {[1, 2, 3].map(count => <SelectItem key={count} value={String(count)}>{count}</SelectItem>)}
    </Select>
    <p className="text-body-medium text-on-surface-variant">Сменность учитывается в подсказке длительности.</p>
  </section>;
  const exceptions = <section className="plan-calendar-card">
    <h3 className="text-title-medium">Исключения стройки</h3>
    <Exceptions days={days} onDay={readOnly ? undefined : setEditor} />
    {!readOnly && <div><Button onClick={() => setEditor('')}>Добавить исключение…</Button></div>}
  </section>;
  return <div className="grid gap-4">
    <PageHeader title="График" subtitle="Монтажный корпус · Календарь стройки" actions={!readOnly &&
      <Button variant="filled" onClick={() => setNotice(scenario === 'conflict'
        ? 'Календарь стройки тем временем изменили. Перечитайте его.' : 'Календарь стройки сохранён.')}>Сохранить календарь</Button>} />
    {notice && <Banner tone={scenario === 'conflict' ? 'warning' : 'success'}>{notice}</Banner>}
    {variant === 'year' && <div className="grid gap-4">{settings}{exceptions}</div>}
    {variant === 'quarter' && <div className="plan-calendar-site-split">{settings}{exceptions}</div>}
    {variant === 'exceptions' && <>
      <Tabs id="plan-calendar-site" aria-label="Настройки календаря стройки" value={view} onChange={setView}
        items={[{ value: 'week', label: 'Неделя и смены' }, { value: 'days', label: `Исключения (${Object.keys(days).length})` }]} />
      {view === 'week' ? settings : exceptions}
    </>}
    {editor !== null && <Modal open title={editor ? dateLabel(editor) : 'Добавить исключение стройки'}
      onOpenChange={open => { if (!open) setEditor(null); }}>
      <DayEditor seedDate={editor} year={2027} days={days} readOnly={false} inline onSave={(date, kind) => {
        const draft = { ...days };
        if (kind) draft[date] = kind;
        else delete draft[date];
        setDays(draft);
        setEditor(null);
      }} />
    </Modal>}
  </div>;
}

export function PlanCalendars({ params }: { params: URLSearchParams }) {
  const param = params.get('page') ?? '';
  const variant = Object.hasOwn(variants, param) ? param as Variant : 'year';
  const [area, setArea] = useState<Area>(params.get('view') === 'site' ? 'site' : 'company');
  const [scenario, setScenario] = useState<Scenario>('filled');
  return <div className="plan-calendar-demo">
    <header className="plan-calendar-demo-bar">
      <h1 className="text-title-large">{variants[variant]}</h1>
      <Select label="Сценарий" density="dense" value={scenario} onValueChange={value => setScenario(value as Scenario)}>
        {Object.entries(scenarios).map(([id, label]) => <SelectItem key={id} value={id}>{label}</SelectItem>)}
      </Select>
    </header>
    <main className="plan-calendar-demo-body">
      <Tabs id="plan-calendar-area" aria-label="Экран календаря" items={[...areas]} value={area} onChange={setArea} />
      {area === 'company' ? <Company key={scenario} variant={variant} scenario={scenario} /> :
        <Site key={scenario} variant={variant} scenario={scenario} />}
    </main>
  </div>;
}
