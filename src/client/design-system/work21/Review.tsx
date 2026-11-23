import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { CountText } from '@/shared/ui/CountText';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { ArrowBack, Assignment, DarkMode, Group, LightMode } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { useTheme } from '@/shared/ui/themeContext';
import { Card } from './Card';
import { Queue, QueueFilters } from './Queue';
import { emptyFilters, matchesScope, reports, scenarios, siteName, variants, type Report, type Scenario, type SiteId, type Variant } from './model';

export function Work21() {
  const { resolvedTheme } = useTheme();
  return <ul className="space-y-3">
    {variants.map(variant => <li key={variant.id}>
      <a className="text-primary underline" href={`work21.html?variant=${variant.id}&theme=${resolvedTheme}`}>{variant.name}</a>
      <p className="text-body-medium text-on-surface-variant">{variant.summary}</p>
    </li>)}
  </ul>;
}

function CrewPreview({ site, contextual = false, onOpenReport }: {
  site?: SiteId;
  contextual?: boolean;
  onOpenReport: (report: Report) => void;
}) {
  if (site === 'substation') return <EmptyState icon={<Icon icon={Group} size={24} />}
    title="Нет расхождений в бригадах" description={siteName(site)} />;

  const rows = [
    { report: reports[0], volume: '48 м' },
    { report: reports[1], volume: '45 м' },
  ];
  const columns: DataGridColumn<(typeof rows)[number]>[] = [
    { key: 'employee', label: 'Сотрудник', rowHeader: true, wrap: true, cell: row => row.report.employee },
    { key: 'volume', label: 'Объём', align: 'right' },
  ];
  return <div className="space-y-4">
    {!contextual && <h2 className="text-title-medium">Сверка бригад</h2>}
    <p className="text-body-small text-on-surface-variant">Предпросмотр размещения панели Work‑18</p>
    <div className="space-y-1">
      <p className="text-body-large font-medium">Прокладка кабеля ВВГнг</p>
      <p className="text-body-medium text-on-surface-variant">Терминал Восточный · 21.11.2026 · ☾ ночь</p>
      <StatusText tone="warning" icon={Group}>Есть расхождение в объёмах</StatusText>
    </div>
    <DataGrid columns={columns} rows={rows} density="compact" onRowOpen={row => onOpenReport(row.report)} rowKey={row => row.report.id} />
  </div>;
}

function WorkSurface({ variant, scenario }: { variant: Variant; scenario: Scenario }) {
  const initialCard = ['history', 'draft', 'not-found', 'stale', 'decided'].includes(scenario);
  const activeQueue = scenario !== 'not-assigned' && scenario !== 'nothing-pending';
  const [data, setData] = useState(reports);
  const [filters, setFilters] = useState(emptyFilters);
  const [selected, setSelected] = useState<string | null>(initialCard || (variant === 'b' && activeQueue) ? '1' : null);
  const [section, setSection] = useState<'queue' | 'crew'>('queue');
  const [crew, setCrew] = useState<SiteId | null>(null);
  const [home, setHome] = useState(false);
  const card = data.find(report => report.id === selected);
  const cardScenario = home ? 'review' : scenario;
  const pending = activeQueue ? data.filter(report => report.status === 'submitted') : [];
  const scoped = pending.filter(report => matchesScope(report, filters));
  const filtered = scoped.filter(report => !filters.mark || report.marks.some(mark => mark.code === filters.mark));
  const boundary = scenario === 'reader' ? 'все стройки' : `отчёты ваших строек (${scenario === 'not-assigned' ? 0 : 2})`;
  const empty = scenario === 'not-assigned' ? scenario : pending.length > 0 ? 'filtered-out' : 'nothing-pending';
  const sideBySide = variant === 'b' && !(initialCard && !home && ['history', 'draft', 'not-found'].includes(scenario));
  const onlyCard = !!card && !sideBySide;

  function open(report: Report) {
    setSelected(report.id);
    setCrew(null);
    setSection('queue');
    if (initialCard && !['stale', 'decided'].includes(scenario)) setHome(true);
  }

  function back() {
    setSelected(null);
    setCrew(null);
    if (initialCard) setHome(true);
  }

  const cardView = card && <Card key={card.id} report={card} variant={variant} embedded={sideBySide} scenario={cardScenario} onBack={back}
    onCrew={() => setCrew(card.site)} onUpdate={updated => setData(current => current.map(r => r.id === updated.id ? updated : r))} />;
  const queue = <>
    <QueueFilters source={data} scoped={scoped} filters={filters} onChange={setFilters} canReset={filtered.length > 0} />
    <div className={sideBySide ? 'work21-split' : ''}>
      <Queue rows={filtered} variant={variant} empty={empty} selected={selected ?? undefined} onOpen={open}
        onCrew={setCrew} onReset={() => setFilters(emptyFilters)} />
      {sideBySide && <div className="min-w-0">
        {crew ? <section className="work21-card work21-card-inset">
          <PageHeader icon={<IconButton icon={ArrowBack} label="К отчёту" onClick={() => setCrew(null)} />}
            title="Сверка бригад" />
          <CrewPreview site={crew} contextual onOpenReport={open} />
        </section> : cardView ?? <EmptyState icon={<Icon icon={Assignment} size={24} />}
          title={filtered.length ? 'Выберите отчёт' : 'Нет отчёта для просмотра'} />}
      </div>}
    </div>
  </>;

  return <div className="work21-app">
    <div className="work21-rail">
      <span className="work21-rail-icon"><Icon icon={Assignment} size={24} /></span>
      <span className="text-label-small">Учёт работ</span>
    </div>
    <section aria-label="Приёмка отчётов" className="work21-page">
      {onlyCard ? cardView : <>
        <PageHeader title="Приёмка" subtitle={boundary}
          badge={pending.length > 0 && <CountText>{pending.length} ждут решения</CountText>} />
        {variant === 'a' && <Tabs id="work21-queue" aria-label="Разделы приёмки" value={section} onChange={setSection}
          items={[{ value: 'queue', label: 'Отчёты' }, { value: 'crew', label: 'Сверка бригад', icon: Group }]} />}
        {variant === 'a' ? <TabPanel id="work21-queue" value={section} className="space-y-4">
          {section === 'queue' ? queue : <CrewPreview onOpenReport={open} />}
        </TabPanel> : queue}
      </>}
    </section>
    {variant === 'c' && crew && <SideSheet variant="modal" open title="Сверка бригад" onClose={() => setCrew(null)}>
      <CrewPreview site={crew} contextual onOpenReport={open} />
    </SideSheet>}
  </div>;
}

export function Review() {
  const params = new URLSearchParams(location.search);
  const [variant, setVariant] = useState(variants.find(v => v.id === params.get('variant')) ?? variants[0]);
  const [scenario, setScenario] = useState(scenarios.find(s => s.id === params.get('scenario')) ?? scenarios[0]);
  const { resolvedTheme, setTheme } = useTheme();
  function remember(key: string, value: string) {
    const url = new URL(location.href);
    url.searchParams.set(key, value);
    history.replaceState(history.state, '', url);
  }

  return <main className="work21-review">
    <header className="work21-heading">
      <div>
        <a className="text-label-large text-primary underline" href={`./?section=work21&theme=${resolvedTheme}`}>BHS.CRG UI</a>
        <h1 className="text-headline-medium">Р4 · Приёмка отчётов</h1>
      </div>
      <IconButton icon={resolvedTheme === 'dark' ? LightMode : DarkMode}
        label={resolvedTheme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
        onClick={() => {
          const theme = resolvedTheme === 'dark' ? 'light' : 'dark';
          setTheme(theme);
          remember('theme', theme);
        }} />
    </header>
    <div className="work21-toolbar">
      <nav className="flex flex-wrap gap-1" aria-label="Варианты макета">
        {variants.map(item => <Button key={item.id} variant={variant === item ? 'tonal' : 'text'}
          aria-current={variant === item ? 'page' : undefined}
          onClick={() => { setVariant(item); remember('variant', item.id); }}>{item.name}</Button>)}
      </nav>
      <Select label="Сценарий" density="dense" value={scenario.id} onValueChange={id => {
        const item = scenarios.find(s => s.id === id);
        if (item) { setScenario(item); remember('scenario', item.id); }
      }}>
        {scenarios.map(item => <SelectItem key={item.id} value={item.id}>{item.label}</SelectItem>)}
      </Select>
    </div>
    <p className="text-body-medium text-on-surface-variant">{variant.summary} Данные демонстрационные.</p>
    <WorkSurface key={`${variant.id}-${scenario.id}`} variant={variant.id} scenario={scenario.id} />
  </main>;
}
