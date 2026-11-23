import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { Icon } from '@/shared/ui/Icon';
import { Add, Assignment, DarkMode, Group, Home, LightMode, Schedule } from '@/shared/ui/icons';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { useTheme } from '@/shared/ui/themeContext';
import { newLocalId } from '@/shared/utils/localId';
import { ReportForm } from './ReportForm';
import {
  assignmentBoundary, candidates, dateText, draft, nightSuggestion, noAssignments, reportFor, sampleReports, scenarios,
  shiftText, siteName, sites, statuses, variants, type Report, type Scenario, type Variant,
} from './model';

function ReportStatus({ report }: { report: Report }) {
  const tone = report.status === 'returned' ? 'warning' : report.status === 'accepted' ? 'success' : 'neutral';
  return <StatusText tone={tone} icon={report.status === 'draft' ? Schedule : Assignment}>{statuses[report.status]}</StatusText>;
}

function ReportList({ variant, reports, onOpen }: { variant: Variant; reports: Report[]; onOpen: (report: Report) => void }) {
  const [tab, setTab] = useState<'current' | 'history'>('current');
  const columns: DataGridColumn<Report>[] = [
    { key: 'date', label: 'Дата и смена', rowHeader: true, cell: report => <span className="font-medium">
      {dateText(report.date)} · {shiftText[report.shift]}
    </span> },
    { key: 'site', label: 'Стройка', wrap: true, cell: report => siteName(report.site) },
    { key: 'status', label: 'Статус', cell: report => <ReportStatus report={report} /> },
    { key: 'note', label: 'Автоподача или комментарий', wrap: true, cell: report => report.note ?? '—' },
  ];
  const grid = (rows: Report[]) => <DataGrid columns={columns} rows={rows} rowKey={report => report.id}
    density="compact" onRowOpen={onOpen} />;

  if (variant === 'a') return grid(reports);
  if (variant === 'b') return <div className="work22-report-cards">
    {reports.map(report => <Card key={report.id} title={`${dateText(report.date)} · ${shiftText[report.shift]}`}
      variant={report.status === 'returned' ? 'filled' : 'outlined'} onClick={() => onOpen(report)}>
      <div className="space-y-2">
        <p className="text-body-medium">{siteName(report.site)}</p>
        <ReportStatus report={report} />
        {report.note && <p className="text-body-small text-on-surface-variant">{report.note}</p>}
      </div>
    </Card>)}
  </div>;
  return <div className="space-y-4">
    <Tabs id="work22-reports" aria-label="Группы отчётов" value={tab} onChange={setTab}
      items={[{ value: 'current', label: 'Требуют внимания' }, { value: 'history', label: 'Поданные и принятые' }]} />
    <TabPanel id="work22-reports" value={tab}>{grid(reports.filter(report =>
      (report.status === 'draft' || report.status === 'returned') === (tab === 'current')))}</TabPanel>
  </div>;
}

function EmployeeDialog({ onClose, onPick }: { onClose: () => void; onPick: (employee: string) => void }) {
  const [selected, setSelected] = useState(new Set<string>());
  const [search, setSearch] = useState('');
  const choice = candidates.find(candidate => selected.has(candidate.employeeId));
  return <Modal open wide title="Ввести за сотрудника" onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit /><Button variant="filled" disabled={!choice} onClick={() => { if (choice) onPick(choice.name); }}>
      Продолжить
    </Button></>}>
    <EmployeePicker candidates={candidates.filter(candidate => candidate.name.toLowerCase().includes(search.toLowerCase()))}
      search={search} onSearch={setSearch} selected={selected} refused={{}}
      onChange={next => setSelected(new Set([...next].filter(id => !selected.has(id)).slice(-1)))} />
  </Modal>;
}

function Surface({ variant, scenario, startForm }: { variant: Variant; scenario: Scenario; startForm: boolean }) {
  const initial = reportFor(scenario);
  const [reports, setReports] = useState(() => sampleReports.map(report => report.id === initial.id ? initial : report));
  const [current, setCurrent] = useState<Report | null>(startForm ? initial : null);
  const [employeeDialog, setEmployeeDialog] = useState(false);
  const [isNew, setIsNew] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [confirmLeave, setConfirmLeave] = useState<(() => void) | null>(null);
  const manager = scenario === 'manager';
  const boundary = scenario === 'unassigned' ? noAssignments : assignmentBoundary;
  const suggested = scenario === 'night' ? nightSuggestion : sites[0].suggested;

  function open(report: Report) {
    setCurrent(report);
    setIsNew(false);
    setDirty(false);
  }

  function create(employee?: string) {
    setEmployeeDialog(false);
    setIsNew(true);
    setCurrent({ id: newLocalId(), site: sites[0].id, date: suggested.date, shift: suggested.shift, status: 'draft',
      employee, hours: '', lines: [] });
  }

  function save(report: Report) {
    setCurrent(report);
    setReports(rows => rows.some(row => row.id === report.id)
      ? rows.map(row => row.id === report.id ? report : row) : [report, ...rows]);
    setIsNew(false);
    setDirty(false);
  }

  function navigate(then: () => void) {
    if (dirty) setConfirmLeave(() => then);
    else then();
  }

  const label = suggested.label;
  return <div className="work22-app">
    <nav className="work22-rail" aria-label="Разделы">
      <div className="work22-rail-item"><Icon icon={Home} size={24} />Главная</div>
      <div className="work22-rail-item" aria-current="page"><Icon icon={Assignment} size={24} filled />Работы</div>
    </nav>
    <section className="work22-page" aria-label="Веб-ввод отчётов">
      {current ? <ReportForm key={current.id} variant={variant} scenario={scenario} report={current} isNew={isNew}
        onChange={report => { setCurrent(report); setDirty(true); }} onSave={save}
        onBack={() => navigate(() => { setCurrent(null); setDirty(false); })}
        onOpenExisting={() => navigate(() => open(reports.find(report => report.id === draft.id) ?? draft))} /> : <>
        <PageHeader title={manager ? 'Ввод отчётов' : 'Мои отчёты'} subtitle={boundary.text}
          actions={manager
            ? <Button variant="filled" icon={<Icon icon={Group} size={18} />} onClick={() => setEmployeeDialog(true)}>Ввести за сотрудника</Button>
            : !boundary.reason && <>
              <PrimaryActionButton action={{ id: 'work-entry', label, words: 'отчёт', icon: Add,
                to: '/work/reports/mine', permission: 'work.report.own' }} onClick={() => create()} />
              <Button variant="filled" className="md:hidden" icon={<Icon icon={Add} size={18} />} onClick={() => create()}>{label}</Button>
            </>} />
        {boundary.reason && <EmptyState icon={<Icon icon={Assignment} size={24} />}
          title="Вы не назначены ни на одну стройку" description="Обратитесь к менеджеру проекта." />}
        {!manager && <ReportList variant={variant} reports={reports} onOpen={open} />}
        {manager && <EmptyState icon={<Icon icon={Group} size={24} />} title="Ввод за сотрудника"
          description="Выберите сотрудника, чтобы открыть доступные ему стройки." />}
      </>}
    </section>
    {employeeDialog && <EmployeeDialog onClose={() => setEmployeeDialog(false)} onPick={create} />}
    {confirmLeave && <Modal open title="Уйти без сохранения?" onOpenChange={open => { if (!open) setConfirmLeave(null); }}
      footer={<><ModalExit>Остаться</ModalExit><Button variant="filled" onClick={() => { confirmLeave(); setConfirmLeave(null); }}>
        Уйти без сохранения
      </Button></>}>Изменения в отчёте будут потеряны.</Modal>}
  </div>;
}

export function EntryMockup() {
  const params = new URLSearchParams(location.search);
  const [variant, setVariant] = useState<Variant>(variants.find(item => item.id === params.get('page'))?.id ?? 'a');
  const [scenario, setScenario] = useState<Scenario>(scenarios.find(item => item.id === params.get('scenario'))?.id ?? 'installer');
  const { resolvedTheme, setTheme } = useTheme();
  const startForm = params.get('view') === 'form' || ['stale', 'required', 'photos', 'closed'].includes(scenario);
  const remember = (key: string, value: string) => {
    const url = new URL(location.href);
    url.searchParams.set(key, value);
    history.replaceState(history.state, '', url);
  };

  return <main className="work22-mockup">
    <header className="work22-toolbar">
      <div>
        <a className="text-label-large text-primary underline"
          href={`./?section=mockups&project=work-22-web-entry-ui&page=${variant}&theme=${resolvedTheme}`}>BHS.CRG UI</a>
        <h1 className="text-headline-medium">Р3 · Веб-ввод отчёта</h1>
      </div>
      <IconButton icon={resolvedTheme === 'light' ? DarkMode : LightMode}
        label={resolvedTheme === 'light' ? 'Включить тёмную тему' : 'Включить светлую тему'}
        onClick={() => {
          const theme = resolvedTheme === 'light' ? 'dark' : 'light';
          setTheme(theme);
          remember('theme', theme);
        }} />
    </header>
    <div className="work22-toolbar">
      <nav className="flex flex-wrap gap-1" aria-label="Варианты макета">
        {variants.map(item => <Button key={item.id} variant={variant === item.id ? 'tonal' : 'text'}
          aria-current={variant === item.id ? 'page' : undefined}
          onClick={() => { setVariant(item.id); remember('page', item.id); }}>{item.title}</Button>)}
      </nav>
      <Select label="Сценарий" density="dense" value={scenario} onValueChange={id => {
        const found = scenarios.find(item => item.id === id);
        if (found) { setScenario(found.id); remember('scenario', id); }
      }}>
        {scenarios.map(item => <SelectItem key={item.id} value={item.id}>{item.label}</SelectItem>)}
      </Select>
    </div>
    <Surface key={`${variant}-${scenario}`} variant={variant} scenario={scenario} startForm={startForm} />
  </main>;
}
