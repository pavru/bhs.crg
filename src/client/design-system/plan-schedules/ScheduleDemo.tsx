import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { Add, ArrowBack, CalendarMonth, Print } from '@/shared/ui/icons';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tabs } from '@/shared/ui/Tabs';
import { newLocalId } from '@/shared/utils/localId';
import { EmployeeDialog, LinkDialog, NewSchedule, PrintDialog, RebuildDialog } from './Dialogs';
import { durationText, employeeNames, initialLinks, schedules, sections, shortDate, variants, warningLabels,
  wholeSite, workTypes, type Variant, type Warning, type Work } from './sample';
import { Timeline } from './Timeline';
import { EditFields, WorkList } from './WorkList';

const modes = [
  { value: 'normal', label: 'Менеджер проекта' },
  { value: 'readonly', label: 'Только чтение' },
  { value: 'conflict', label: 'Конфликты и цикл' },
  { value: 'gap', label: 'Не заполнен календарь' },
  { value: 'empty', label: 'Графиков пока нет' },
] as const;
type Mode = (typeof modes)[number]['value'];
type Dialog = 'estimate' | 'manual' | 'rebuild' | 'employees' | 'links' | 'print' | 'add' | 'work' | 'calendar';
type Filter = 'all' | Warning;
const warningFilters: { value: Warning; label: string }[] = [
  { value: 'dependency', label: 'Нарушения зависимостей' },
  { value: 'day-off', label: 'Окончание на выходном' },
  { value: 'no-dates', label: 'Без сроков' },
  { value: 'calendar', label: 'Пробелы календаря' },
];
const newWork = (name: string, section: string, volume: string, unit: string): Work =>
  ({ id: newLocalId(), name, section, volume, unit, start: '', end: '', duration: null, employeeIds: [], warnings: ['no-dates'] });

function WorkDetails({ work, draft, predecessors, onEdit, onChange, onEmployees, onLinks, onSave, onCancel }: {
  work: Work; draft: Work | null; onEdit?: () => void; onChange: (patch: Partial<Work>) => void;
  predecessors: { name: string; graph: string; lag: string }[];
  onEmployees: () => void; onLinks: () => void; onSave: () => void; onCancel: () => void;
}) {
  return <div className="grid gap-4">
    <div>
      <h3 className="text-title-large">{work.name}</h3>
      <p className="text-body-medium text-on-surface-variant">{work.section}</p>
    </div>
    {draft ? <EditFields work={draft} onChange={onChange} onEmployees={onEmployees} /> :
      <dl className="plan-schedule-fields text-body-medium">
        <div><dt>Начало</dt><dd>{shortDate(work.start)}</dd></div>
        <div><dt>Окончание</dt><dd>{shortDate(work.end)}</dd></div>
        <div><dt>Длительность</dt><dd>{durationText(work)}</dd></div>
        <div><dt>Объём</dt><dd>{work.volume} {work.unit}</dd></div>
        <div><dt>Исполнители</dt><dd>{employeeNames(work) || 'Не назначены'}</dd></div>
      </dl>}
    {!!work.warnings.length && <Banner tone="warning">
      <ul>{work.warnings.map(warning => <li key={warning}>{warningLabels[warning]}</li>)}</ul>
    </Banner>}
    {!draft && <section className="grid gap-2">
      <h4 className="text-title-medium">Предшественники</h4>
      {predecessors.length ? <ul className="grid gap-2 text-body-medium">
        {predecessors.map(item => <li key={`${item.graph}/${item.name}`}>
          {item.name}<span className="block text-body-small text-on-surface-variant">{item.graph} · {item.lag} раб. дн.</span>
        </li>)}
      </ul> : <p className="text-body-medium text-on-surface-variant">Связей нет.</p>}
    </section>}
    <div className="flex flex-wrap gap-2">
      {draft ? <><Button variant="filled" onClick={onSave}>Сохранить работу</Button>
        <Button variant="text" onClick={onCancel}>Отмена</Button></> :
        <>{onEdit && <Button variant="tonal" onClick={onEdit}>Изменить работу</Button>}
          <Button variant="outlined" onClick={onLinks}>{onEdit ? 'Изменить связи' : 'Открыть связи'}</Button></>}
    </div>
  </div>;
}

function AddWorkDialog({ onClose, onAdd }: { onClose: () => void; onAdd: (work: Work) => void }) {
  const [name, setName] = useState(workTypes[0].name);
  const [section, setSection] = useState(wholeSite);
  const [volume, setVolume] = useState('');
  const unit = workTypes.find(type => type.name === name)!.unit;
  return <Modal open title="Добавить работу" onOpenChange={open => !open && onClose()}
    footer={<><ModalExit /><ModalPrimary onClick={() => onAdd(newWork(name, section, volume, unit))}>Добавить</ModalPrimary></>}>
    <div className="grid gap-4">
      <Select label="Вид работы" value={name} onValueChange={setName}>
        {workTypes.map(type => <SelectItem key={type.name} value={type.name}>{type.name}</SelectItem>)}
      </Select>
      <Select label="Раздел" value={section} onValueChange={setSection}>
        {sections.map(item => <SelectItem key={item} value={item}>{item}</SelectItem>)}
      </Select>
      <NumberInput label="Объём" value={volume} unit={unit} onChange={setVolume} />
    </div>
  </Modal>;
}

function ScheduleScreen({ variant, mode }: { variant: Variant; mode: Mode }) {
  const [all, setAll] = useState(() => mode === 'empty' ? [] : schedules.map(schedule => ({
    ...schedule, works: mode === 'gap' && schedule.id === 'electric'
      ? schedule.works.map(row => row.id === 'checks' ? { ...row, end: '2027-01-11', warnings: ['no-dates', 'calendar'] as Warning[] } : row)
      : schedule.works,
  })));
  const [scheduleId, setScheduleId] = useState<string | null>(mode === 'empty' ? null : 'electric');
  const [selected, setSelected] = useState<string | null>(variant === 'b' ? 'cable' : null);
  const [draft, setDraft] = useState<Work | null>(null);
  const [linkWork, setLinkWork] = useState<Work | null>(null);
  const [view, setView] = useState<'list' | 'timeline'>('list');
  const [section, setSection] = useState('all');
  const [filter, setFilter] = useState<Filter>('all');
  const [links, setLinks] = useState(initialLinks);
  const [rebuilt, setRebuilt] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [returnDialog, setReturnDialog] = useState<Dialog | null>(null);
  const [stale, setStale] = useState<'new' | 'shown' | 'reviewed'>('new');
  const [notice, setNotice] = useState('');
  const readOnly = mode === 'readonly';
  const schedule = all.find(item => item.id === scheduleId);
  const rows = schedule?.works ?? [];
  const active = rows.find(work => work.id === selected);
  const changeDraft = (patch: Partial<Work>) => setDraft(work => work && { ...work, ...patch });
  const updateRows = (next: Work[]) => setAll(previous => previous.map(item => item.id === scheduleId ? { ...item, works: next } : item));
  const eligible = rows.filter(row => section === 'all' || row.section === section);
  const visible = eligible.filter(row => filter === 'all' || row.warnings.includes(filter)
    || (filter === 'dependency' && links.some(link => link.predecessor === row.id
      && eligible.some(successor => successor.id === link.successor && successor.warnings.includes(filter)))));

  function selectSchedule(id: string | null) {
    setScheduleId(id);
    setDraft(null);
    setSelected(null);
    setFilter('all');
    setSection('all');
    setView('list');
    setNotice('');
  }

  function choose(work: Work) {
    if (work.id !== selected) setDraft(null);
    setSelected(work.id);
    if (variant === 'a' && !readOnly) setDraft(work);
    if (variant === 'c' || (variant === 'a' && readOnly)) setDialog('work');
    setStale('new');
  }

  function save() {
    if (!draft) return;
    if (mode === 'conflict' && stale === 'new') { setStale('shown'); return; }
    updateRows(rows.map(work => work.id === draft.id ? draft : work));
    setDraft(null);
    setNotice('Работа сохранена.');
    setStale('new');
  }

  function simpleClose() { setDialog(null); }
  function employees() { setReturnDialog(dialog); setDialog('employees'); }
  function linksFor(work: Work) { setReturnDialog(dialog); setLinkWork(work); setDialog('links'); }
  const predecessors = (work: Work) => links.filter(link => link.successor === work.id).flatMap(link => {
    const graph = all.find(item => item.works.some(row => row.id === link.predecessor));
    const row = graph?.works.find(item => item.id === link.predecessor);
    return row && graph ? [{ name: row.name, graph: graph.name, lag: link.lag }] : [];
  });
  const details = (work: Work) => <WorkDetails work={work} draft={draft?.id === work.id ? draft : null} predecessors={predecessors(work)}
    onEdit={readOnly ? undefined : () => setDraft(work)} onChange={changeDraft}
    onEmployees={employees} onLinks={() => linksFor(work)} onSave={save} onCancel={() => setDraft(null)} />;

  return <section className="plan-schedule-app">
    <div className="plan-schedule-screen">
      <PageHeader title="График стройки" subtitle="Бизнес-центр «Речной»" actions={
        <Button variant="outlined" icon={<Icon icon={CalendarMonth} size={18} />} onClick={() => setDialog('calendar')}>
          Календарь стройки
        </Button>} />
      {!schedule ? <>
        {!readOnly && <div className="flex flex-wrap gap-2">
          <Button variant="filled" onClick={() => setDialog('estimate')}>Построить из сметы</Button>
          <Button variant="outlined" onClick={() => setDialog('manual')}>Новый график</Button>
        </div>}
        {all.length ? <DataGrid columns={[
          { key: 'name', label: 'График', rowHeader: true, wrap: true },
          { key: 'source', label: 'Источник', wrap: true },
          { key: 'works', label: 'Работ', width: 'w-24', cell: item => item.works.length },
        ]} rows={all} rowKey={item => item.id} onRowOpen={item => selectSchedule(item.id)} />
          : <p className="plan-schedule-panel text-body-medium text-on-surface-variant">У стройки пока нет графиков.</p>}
      </> : <>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <Button variant="text" icon={<Icon icon={ArrowBack} size={18} />} onClick={() => selectSchedule(null)}>Все графики</Button>
          <div className="flex flex-wrap gap-2">
            {!readOnly && <Button variant="outlined" icon={<Icon icon={Add} size={18} />} onClick={() => setDialog('add')}>Добавить работу</Button>}
            <Button variant="outlined" icon={<Icon icon={Print} size={18} />} onClick={() => setDialog('print')}>Печать списка</Button>
          </div>
        </div>
        <div className="plan-schedule-heading">
          <div><h2 className="text-headline-small">{schedule.name}</h2>
            <p className="text-body-medium text-on-surface-variant">{schedule.source} · Работ: {rows.length}</p></div>
          <div className="plan-schedule-switch">
            <Tabs id="schedule-view" aria-label="Вид графика" variant={variant === 'a' ? 'segmented' : 'primary'}
              items={[{ value: 'list', label: 'Список' }, { value: 'timeline', label: 'Диаграмма' }]}
              value={view} onChange={setView} />
          </div>
        </div>
        {schedule.id === 'electric' && !rebuilt && <Banner tone="warning" actions={readOnly ? undefined : [{
          label: 'Предпросмотр перестроения', onClick: () => setDialog('rebuild'),
        }]}>Распределение сметы изменилось после построения графика.</Banner>}
        {schedule.source !== 'Ручной график' && <p className="text-body-medium text-on-surface-variant">
          Не вошли {rebuilt ? 3 : 2} несопоставленные строки сметы.
        </p>}
        {mode === 'gap' && <Banner tone="warning">{warningLabels.calendar}.</Banner>}
        {stale === 'shown' && dialog !== 'work' && <Banner tone="warning" actions={[{
          label: 'Перечитать', onClick: () => setStale('reviewed'),
        }]}>Работу тем временем изменили. Перечитайте данные. Ваш ввод сохранён.</Banner>}
        {notice && <p role="status" className="text-body-medium text-on-surface-variant">{notice}</p>}
        <div className="plan-schedule-filters">
          <Select label="Раздел" value={section} onValueChange={setSection}>
            <SelectItem value="all">Все разделы</SelectItem>
            {sections.map(item => <SelectItem key={item} value={item}>{item}</SelectItem>)}
          </Select>
          <div className="flex flex-wrap items-center gap-2" aria-label="Фильтр предупреждений">
            <Button aria-pressed={filter === 'all'} variant={filter === 'all' ? 'tonal' : 'text'} onClick={() => setFilter('all')}>Все работы</Button>
            {warningFilters.map(item => {
              const count = eligible.filter(work => work.warnings.includes(item.value)).length;
              return count > 0 && <Button key={item.value} aria-pressed={filter === item.value}
                variant={filter === item.value ? 'tonal' : 'outlined'} onClick={() => setFilter(item.value)}>
                {item.label} · {count}
              </Button>;
            })}
          </div>
        </div>
        <div className={view === 'timeline' ? 'plan-schedule-list-view min-w-0' : 'min-w-0'}>
          <WorkList variant={variant} rows={visible} selected={selected} draft={draft} onChoose={choose}
            onChange={changeDraft} onEmployees={employees} onLinks={linksFor} onSave={save} onCancel={() => setDraft(null)}
            side={<aside className="plan-schedule-panel">{active && visible.includes(active)
              ? details(active) : <p>Выберите работу из списка.</p>}</aside>} />
        </div>
        {view === 'timeline' && <div className="plan-schedule-timeline-view">
          <Timeline rows={visible} links={links} onOpen={work => { setSelected(work.id); setView('list'); }} />
        </div>}
      </>}
    </div>
    {(dialog === 'estimate' || dialog === 'manual') && <NewSchedule fromEstimate={dialog === 'estimate'} onClose={simpleClose}
      onCreate={(name, source, works) => {
        const id = newLocalId();
        const rows = works.map(work => ({ ...work, id: newLocalId() }));
        setAll(previous => [...previous, { id, name, source, works: rows }]);
        selectSchedule(id);
        simpleClose();
      }} />}
    {dialog === 'rebuild' && <RebuildDialog stale={mode === 'conflict'} onClose={simpleClose} onApply={() => {
      updateRows([...rows.filter(work => work.id !== 'panels').map(work => work.id === 'trays'
        ? { ...work, volume: mode === 'conflict' ? '560' : '600' } : work),
      newWork('Монтаж светильников', 'Корпус 2', '12', 'шт.')]);
      setLinks(previous => previous.filter(link => link.predecessor !== 'panels' && link.successor !== 'panels'));
      setRebuilt(true);
      simpleClose();
    }} />}
    {dialog === 'print' && schedule && <PrintDialog name={schedule.name} rows={rows} onClose={simpleClose} />}
    {dialog === 'add' && <AddWorkDialog onClose={simpleClose} onAdd={work => { updateRows([...rows, work]); simpleClose(); choose(work); }} />}
    {dialog === 'employees' && draft && <EmployeeDialog selected={draft.employeeIds} onClose={() => setDialog(returnDialog)}
      onApply={employeeIds => { changeDraft({ employeeIds }); setDialog(returnDialog); }} />}
    {dialog === 'links' && linkWork && <LinkDialog work={linkWork} local={rows} links={links}
      readOnly={readOnly} conflict={mode === 'conflict'} onClose={() => setDialog(returnDialog)} onApply={setLinks} />}
    {dialog === 'work' && active && <Modal open title="Карточка работы" onOpenChange={open => !open && simpleClose()}>
      {stale === 'shown' && <Banner tone="warning" actions={[{ label: 'Перечитать', onClick: () => setStale('reviewed') }]}>
        Работу тем временем изменили. Перечитайте данные. Ваш ввод сохранён.
      </Banner>}
      {details(active)}
    </Modal>}
    {dialog === 'calendar' && <Modal open title="Календарь стройки" onOpenChange={open => !open && simpleClose()}>
      <p className="text-body-medium">Рабочая неделя · понедельник — пятница. Одна смена.</p>
      <p className="text-body-medium">Нерабочие дни периода · 28–29 ноября, 5–6 декабря.</p>
    </Modal>}
  </section>;
}

export function ScheduleDemo({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState<Variant>(variants.find(item => item.value === params.get('page'))?.value ?? 'a');
  const [mode, setMode] = useState<Mode>('normal');
  return <main className="plan-schedule-demo">
    <header className="plan-schedule-toolbar">
      <Tabs id="plan-schedule-variants" aria-label="Варианты макета" variant="segmented"
        value={variant} onChange={setVariant} items={[...variants]} />
      <Select label="Сценарий" value={mode} onValueChange={value => setMode(value as Mode)}>
        {modes.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
      </Select>
    </header>
    <ScheduleScreen key={`${variant}/${mode}`} variant={variant} mode={mode} />
  </main>;
}
