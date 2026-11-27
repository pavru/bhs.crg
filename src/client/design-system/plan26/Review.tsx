import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Add } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { EstimateWizard } from './EstimateWizard';
import { InvoicePreview } from './InvoicePreview';
import { RequestBrowser } from './RequestBrowser';
import { option, scenarios, starter, variants, type Request, type Scenario, type Variant } from './sample';

const views = [
  { value: 'requests', label: 'Заявки' },
  { value: 'invoice', label: 'Счёт на оплату' },
] as const;
const widths = [
  { value: 'wide', label: 'Широкий' },
  { value: 'narrow', label: 'Узкий' },
] as const;

type View = (typeof views)[number]['value'];
type Width = (typeof widths)[number]['value'];

function remember(name: string, value: string) {
  const url = new URL(location.href);
  url.searchParams.set(name, value);
  history.replaceState(history.state, '', url);
}

function Control<T extends string>({ label, items, value, param, onChange }: {
  label: string; items: readonly { value: T; label: string }[]; value: T; param: string; onChange: (value: T) => void;
}) {
  return <Select label={label} value={value} density="dense" onValueChange={raw => {
    const next = option(items, raw, value);
    onChange(next);
    remember(param, next);
  }}>
    {items.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
  </Select>;
}

function Sandbox({ variant, scenario, view, narrow }: { variant: Variant; scenario: Scenario; view: View; narrow: boolean }) {
  const [requests, setRequests] = useState(starter);
  const [selected, setSelected] = useState(147);
  const [linked, setLinked] = useState<number>();
  const [wizard, setWizard] = useState(false);
  const [version, setVersion] = useState(0);
  const [notice, setNotice] = useState<string>();
  const canCreate = scenario !== 'plan-off';

  function link(id: number | undefined) {
    if (id === linked) return;
    setRequests(previous => previous.map(row => {
      const direction = row.number === id ? 1 : row.number === linked ? -1 : 0;
      return direction === 0 ? row : { ...row, invoices: row.invoices + direction,
        rows: row.rows.map(material => material.id === 'cable' && material.unit === 'м' && material.invoiced !== null
          ? { ...material, invoiced: material.invoiced + 100 * direction } : material) };
    }));
    setLinked(id);
    setNotice(id === undefined ? 'Связь со счётом снята.' : `Счёт связан с заявкой № ${id}.`);
  }
  function save(row: Request) {
    setRequests(previous => previous.map(item => item.number === row.number ? row : item));
    setNotice(`Заявка № ${row.number} обновлена.`);
  }

  return <TabPanel id="p6-view" value={view} className="grid min-w-0 gap-3">
    {notice && <Banner tone="success" actions={[{ label: 'Закрыть', onClick: () => setNotice(undefined) }]}>{notice}</Banner>}
    {view === 'invoice' ? <InvoicePreview variant={variant} scenario={scenario} narrow={narrow}
      requests={requests} linked={linked} onLink={link} /> : <>
      <PageHeader title="Заявки на закупку"
        actions={<Button variant="filled" icon={<Icon icon={Add} size={18} />} disabled={!canCreate} onClick={() => {
          setNotice(undefined); setWizard(true);
        }}>Новая из сметы</Button>} />
      {!canCreate && <Banner tone="info">Для создания заявки нужен модуль «Планирование». Прежние заявки доступны.</Banner>}
      <RequestBrowser key={`${variant}:${version}`} variant={variant} scenario={scenario} narrow={narrow}
        requests={requests} selected={selected} openedAfterCreate={version > 0} onSelect={setSelected} onSave={save} onDelete={id => {
          setRequests(previous => previous.filter(row => row.number !== id));
          setNotice(`Черновик № ${id} удалён.`);
        }} />
    </>}
    {wizard && <EstimateWizard variant={variant} scenario={scenario} narrow={narrow}
      onClose={() => setWizard(false)} onSave={(site, rows) => {
        const number = 148 + version;
        const created: Request = { number, site, rows, status: 'draft',
          date: '2026-11-27', author: 'Анна Волкова', availableToAuthor: true, invoices: 0 };
        setRequests(previous => [created, ...previous]);
        setSelected(number);
        setVersion(previous => previous + 1);
        setWizard(false);
        setNotice(`Черновик № ${number} сохранён.`);
      }} />}
  </TabPanel>;
}

export function Review({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState<Variant>(() => option(variants, params.get('page'), 'a'));
  const [scenario, setScenario] = useState<Scenario>(() => option(scenarios, params.get('scenario'), 'normal'));
  const [width, setWidth] = useState<Width>(() => option(widths, params.get('width'), 'wide'));
  const [view, setView] = useState<View>(() => option(views, params.get('view'), 'requests'));
  const [reset, setReset] = useState(0);

  return <>
    <header className="border-b border-outline-variant bg-surface">
      <div className="mx-auto grid max-w-7xl min-w-0 gap-3 px-4 py-3 md:px-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <a className="focus-ring text-title-medium text-primary" href="./?section=mockups&project=plan-26-purchase-requests">
            BHS.CRG UI · П6
          </a>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="text" onClick={() => setReset(value => value + 1)}>Сбросить пример</Button>
            <div className="w-36"><ThemeToggle label="Тема макета" /></div>
          </div>
        </div>
        <div className="grid min-w-0 items-end gap-2 sm:grid-cols-2 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
          <Control label="Вариант" items={variants} value={variant} onChange={setVariant} param="page" />
          <Control label="Сценарий" items={scenarios} value={scenario} onChange={setScenario} param="scenario" />
          <SegmentedButton aria-label="Ширина макета" items={[...widths]} value={width}
            onChange={next => { setWidth(next); remember('width', next); }} />
        </div>
      </div>
    </header>
    <main className={`@container/p6 mx-auto grid min-w-0 gap-4 px-4 py-5 ${width === 'narrow' ? 'max-w-[390px]' : 'max-w-7xl md:px-6'}`}>
      <Tabs id="p6-view" aria-label="Экран П6" items={[...views]} value={view}
        onChange={next => { setView(next); remember('view', next); }} />
      <Sandbox key={`${scenario}:${reset}`} variant={variant} scenario={scenario} view={view} narrow={width === 'narrow'} />
    </main>
  </>;
}
