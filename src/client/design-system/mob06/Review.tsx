import { useEffect, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Assignment, CloudDone, CloudOff, Person, RestartAlt } from '@/shared/ui/icons';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Switch } from '@/shared/ui/Switch';
import { TextField } from '@/shared/ui/TextField';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { DeliveryMark, ReportList, ReportView, ShiftMeta } from './ReportView';
import { sampleReports, sites, today, variants, type Report, type Variant } from './sample';

const comparisons: { value: Variant; label: string }[] = [
  { value: 'a', label: 'А · Карточка' }, { value: 'b', label: 'Б · Поиск' }, { value: 'c', label: 'В · Шаги' },
];
const devices = [
  { value: 'auto', label: 'По ширине окна' }, { value: 'phone', label: 'Телефон · 390' },
  { value: 'landscape', label: 'Телефон · альбом' }, { value: 'tablet', label: 'Планшет · 1024' },
];
const navigation = [
  { view: 'reports', label: 'Отчёты', icon: Assignment },
  { view: 'connection', label: 'Связь', icon: CloudDone },
  { view: 'profile', label: 'Профиль', icon: Person },
] as const;
type View = (typeof navigation)[number]['view'];

function readReports(storageKey: string): Report[] {
  try {
    const saved = sessionStorage.getItem(storageKey);
    if (saved) return JSON.parse(saved) as Report[];
  } catch { /* Макет остаётся доступным в браузере с закрытым хранилищем. */ }
  return sampleReports();
}

export function Review({ params }: { params: URLSearchParams }) {
  const initial = comparisons.find(choice => choice.value === params.get('page'))?.value ?? 'a';
  const [variant, setVariant] = useState(initial);
  const [device, setDevice] = useState(params.get('device') ?? 'auto');
  const [offline, setOffline] = useState(true);
  const [reset, setReset] = useState(0);
  const storageKey = 'mob06-mockup:' + variant;

  function choose(next: Variant) {
    setVariant(next);
    const url = new URL(location.href);
    url.searchParams.set('page', next);
    history.replaceState(history.state, '', url);
  }

  return <main className="mob-preview">
    <header className="mob-preview-head">
      <div>
        <h1>Полевой ввод</h1>
        <p className="mob-muted">{variants[variant].description}</p>
      </div>
      <div className="mob-preview-theme"><ThemeToggle label="Цветовая тема" /></div>
    </header>
    <div className="mob-preview-tools">
      <SegmentedButton items={comparisons} value={variant} onChange={choose} aria-label="Вариант макета" />
      <Select label="Устройство" density="dense" value={device} onValueChange={setDevice}>
        {devices.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
      </Select>
      <div className="mob-offline-control"><span>Нет связи</span><Switch label="Нет связи" checked={offline} onChange={setOffline} /></div>
      <Button variant="text" icon={<Icon icon={RestartAlt} size={18} />} onClick={() => {
        try { sessionStorage.removeItem(storageKey); } catch { /* Можно сбросить и только текущий экран. */ }
        setReset(previous => previous + 1);
      }}>Сбросить пример</Button>
    </div>
    <div className="mob-stage" data-device={device}>
      <Sandbox key={variant + ':' + reset} storageKey={storageKey} variant={variant} offline={offline} />
    </div>
  </main>;
}

function Sandbox({ storageKey, variant, offline }: { storageKey: string; variant: Variant; offline: boolean }) {
  const [reports, setReports] = useState(() => readReports(storageKey));
  const [selected, setSelected] = useState<string | null>(() => reports[0]?.id ?? null);
  const [pane, setPane] = useState<'list' | 'report'>('list');
  const [view, setView] = useState<View>('reports');
  const [creating, setCreating] = useState(false);
  const report = reports.find(item => item.id === selected);
  const localCount = reports.filter(item => item.status === 'submitted' && item.delivery === 'local').length;

  useEffect(() => {
    try { sessionStorage.setItem(storageKey, JSON.stringify(reports)); } catch { /* Проигрывание остаётся доступно. */ }
  }, [reports, storageKey]);

  function open(id: string) {
    setSelected(id);
    setPane('report');
    setView('reports');
  }
  function makeReport(siteId: string | null, siteName: string) {
    const next: Report = {
      id: crypto.randomUUID(), siteId, siteName, date: today, shift: 'day',
      status: 'draft', delivery: 'local', hours: '', lines: [],
    };
    setReports(previous => [next, ...previous]);
    setCreating(false);
    open(next.id);
  }

  return <div className="mob-app" data-variant={variant}>
    <div className="mob-app-bar">
      <span className="mob-app-name">BHS.CRG</span>
      <span className="mob-network"><Icon icon={offline ? CloudOff : CloudDone} size={20} />{offline ? 'Нет связи' : 'В сети'}</span>
    </div>
    {view === 'reports' && <div className="mob-report-layout" data-pane={pane}>
      <ReportList reports={reports} selected={selected} onOpen={open} onCreate={() => setCreating(true)} />
      {report && <ReportView key={report.id} report={report} variant={variant} offline={offline} focusOnOpen={pane === 'report'} onBack={() => setPane('list')}
        onEnterReport={() => setPane('report')}
        onChange={next => setReports(previous => previous.map(item => item.id === next.id ? next : item))} />}
    </div>}
    {view === 'connection' && <section className="mob-secondary-view">
      <h2>Связь</h2>
      <StatusText icon={offline ? CloudOff : CloudDone}>{offline ? 'Нет связи' : 'В сети'}</StatusText>
      <h3>Ожидают доставки · {localCount}</h3>
      {localCount ? <ul className="mob-work-list">{reports.filter(item => item.status === 'submitted' && item.delivery === 'local').map(item =>
        <li key={item.id}><Button variant="text" multiline className="mob-choice" onClick={() => open(item.id)}>
          <DeliveryMark delivery={item.delivery} /><span>{item.siteName}</span><ShiftMeta report={item} />
        </Button></li>)}</ul> : <p className="mob-muted">Поданных отчётов в очереди нет.</p>}
    </section>}
    {view === 'profile' && <section className="mob-secondary-view">
      <h2>Профиль</h2>
      <div className="mob-profile"><span className="mob-avatar"><Icon icon={Person} size={24} /></span>
        <div><h3>Сергей Мельников</h3><p className="mob-muted">Монтажник</p></div>
      </div>
      <p>БХС · Учёт работ</p>
    </section>}
    <nav className="mob-bottom-nav" aria-label="Разделы приложения">
      {navigation.map(item => <Button key={item.view} variant="text" className="mob-nav-button"
        aria-current={view === item.view ? 'page' : undefined}
        icon={<Icon icon={item.icon} size={20} />} onClick={() => { setView(item.view); if (item.view === 'reports') setPane('list'); }}>
        {item.label}
      </Button>)}
    </nav>
    {creating && <StartReport onCancel={() => setCreating(false)} onCreate={makeReport} />}
  </div>;
}

function StartReport({ onCancel, onCreate }: { onCancel: () => void; onCreate: (id: string | null, name: string) => void }) {
  const [site, setSite] = useState('');
  const [manualName, setManualName] = useState('');
  const manual = site === 'unlisted';
  const fromList = sites.find(item => item.id === site);
  const allowed = !!fromList || manual && !!manualName.trim();
  return <Modal open onOpenChange={open => { if (!open) onCancel(); }} title="Новый отчёт" isDirty={!!manualName.trim()}
    footer={<><ModalExit /><ModalPrimary disabled={!allowed} onClick={() => {
      if (allowed) onCreate(fromList?.id ?? null, fromList?.name ?? manualName.trim());
    }}>Открыть отчёт</ModalPrimary></>}>
    <div className="mob-stack">
      <Select label="Стройка" value={site} onValueChange={setSite} emptyItem="Выберите стройку">
        {sites.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
        <SelectItem value="unlisted">Объекта нет в списке</SelectItem>
      </Select>
      {manual && <TextField label="Название объекта" value={manualName} onChange={e => setManualName(e.target.value)} />}
    </div>
  </Modal>;
}
