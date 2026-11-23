import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { ListItem, LIST_CONTAINER } from '@/shared/ui/ListItem';
import { Modal } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useMedia } from '@/shared/hooks/useMedia';
import { planOffersMockup } from '../live-mockups';
import { Estimate, type Derivative } from './Estimate';
import { OfferCard, OfferStatusText, type OfferAction } from './OfferCard';
import { CreateOfferDialog, OfferDialog } from './OfferDialogs';
import { PriceList } from './PriceList';
import { offers, offerSource, personas, scenes, variantNotes, type Offer, type Persona, type Scene, type Variant } from './sample';

type View = 'offers' | 'estimate' | 'pricing';

function OfferRegistry({ rows, onOpen, onCreate }: { rows: Offer[]; onOpen: (offer: Offer) => void; onCreate?: () => void }) {
  const columns: DataGridColumn<Offer>[] = [
    { key: 'number', label: 'КП', rowHeader: true, cell: row => <>{row.number}<small className="block text-on-surface-variant">Версия {row.version}</small></> },
    { key: 'site', label: 'Стройка', wrap: true, cell: row => row.site },
    { key: 'source', label: 'Источник', cell: offerSource },
    { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
    { key: 'date', label: 'Отправлено', cell: row => row.sentOn ?? '—' },
    { key: 'money', label: 'Итого', align: 'right', cell: row => <>{row.total}{row.incomplete && <small className="block text-on-surface-variant">Неполный итог</small>}</> },
  ];
  return <section className="grid min-w-0 grid-cols-1 gap-3">
    <PageHeader title="Коммерческие предложения" actions={onCreate && <Button variant="filled" onClick={onCreate}>КП по прайс-листу…</Button>} />
    <DataGrid rows={rows} columns={columns} rowKey={row => row.id} onRowOpen={row => onOpen(row)} pinned={1} />
  </section>;
}

function Workspace({ variant, persona, scene, initialView, initialOffer }: {
  variant: Variant; persona: Persona; scene: Scene; initialView: View; initialOffer: string | null;
}) {
  const canRead = persona !== 'estimate';
  const canReadEstimate = persona !== 'reader' && persona !== 'price-editor';
  const canCreate = persona === 'manager' || persona === 'editor' || persona === 'cost';
  const [view, setView] = useState<View>(!canRead ? 'estimate' : initialView === 'estimate' && !canReadEstimate ? 'offers' : initialView);
  const [rows, setRows] = useState(offers);
  const wide = useMedia('(min-width: 1280px)');
  const split = variant === 'split' && wide;
  const [selected, setSelected] = useState<string | null>(initialOffer ?? (split ? 'main' : null));
  const [backToEstimate, setBackToEstimate] = useState(false);
  const [activeAction, setActiveAction] = useState<OfferAction | null>(null);
  const [create, setCreate] = useState<'pricelist' | number | null>(null);
  const [history, setHistory] = useState<Derivative | null>(null);
  const offer = rows.find(item => item.id === selected);
  const nav: { id: View; label: string }[] = [];
  if (canReadEstimate) nav.push({ id: 'estimate', label: 'Сметы' });
  if (canRead) nav.push({ id: 'offers', label: 'Коммерческие предложения' }, { id: 'pricing', label: 'Прайс-лист' });
  function open(item: Offer, fromEstimate = false) { setSelected(item.id); setView('offers'); setBackToEstimate(fromEstimate); }
  function save(item: Offer) { setRows(previous => previous.map(row => row.id === item.id ? item : row)); setActiveAction(null); }
  function openDerivative(item: Derivative) {
    const linked = rows.find(row => row.id === item.id);
    if (item.historical) setHistory(item);
    else if (linked) open(linked, true);
  }
  const createFromPrice = canCreate ? () => setCreate('pricelist') : undefined;
  return <div className="grid min-w-0 gap-4 rounded-lg border border-outline-variant bg-surface p-3 sm:p-5">
    <div className="grid min-w-0 items-start gap-4 md:grid-cols-[12rem_minmax(0,1fr)]">
      <nav className="min-w-0 rounded-md bg-surface-container-low p-2" aria-label="Планирование">
        <h2 className="px-2 pb-1 text-title-small">Планирование</h2>
        <ul className={LIST_CONTAINER}>{nav.map(item => <li key={item.id}><ListItem headline={<span className="whitespace-normal">{item.label}</span>}
          selected={view === item.id} onClick={() => { setView(item.id); if (item.id === 'offers') { setBackToEstimate(false); setSelected(split ? 'main' : null); } }} /></li>)}</ul>
      </nav>
      <main className="grid min-w-0 gap-4">
        {view === 'offers' && canRead && (split ? <div className="grid min-w-0 gap-4 xl:grid-cols-[14rem_minmax(0,1fr)]">
          <section className="min-w-0 rounded-md border border-outline-variant p-2">
            <h2 className="px-2 pb-1 text-title-medium">Коммерческие предложения</h2>
            {createFromPrice && <Button variant="text" onClick={createFromPrice}>КП по прайс-листу…</Button>}
            <ul className={LIST_CONTAINER} aria-label="Реестр КП">{rows.map(row => <li key={row.id}><ListItem headline={row.number}
              supporting={<span>{row.site}<br />{offerSource(row)}<br />{row.total}{row.incomplete ? ' · Неполный итог' : ''}<br />
                <OfferStatusText status={row.status} />{row.sentOn && ` · ${row.sentOn}`}</span>}
              selected={row.id === selected} onClick={() => open(row)} /></li>)}</ul>
          </section>
          {offer && <OfferCard key={offer.id} offer={offer} variant={variant} persona={persona} scene={scene} onAction={setActiveAction} />}
        </div> : offer ? <>
          <div><Button variant="text" onClick={() => { if (backToEstimate) setView('estimate'); else setSelected(null); }}>{backToEstimate ? 'К смете' : 'К списку КП'}</Button></div>
          <OfferCard key={offer.id} offer={offer} variant={variant} persona={persona} scene={scene} onAction={setActiveAction} />
        </> : <OfferRegistry rows={rows} onOpen={open} onCreate={createFromPrice} />)}
        {view === 'pricing' && canRead && <PriceList variant={variant} persona={persona} scene={scene} onCreate={createFromPrice} />}
        {view === 'estimate' && <Estimate variant={variant} canRead={canRead} onOffer={openDerivative} onCreate={canCreate ? setCreate : undefined} />}
      </main>
    </div>
    {offer && activeAction && <OfferDialog offer={offer} action={activeAction} scene={scene} onApply={save} onClose={() => setActiveAction(null)} />}
    {create !== null && <CreateOfferDialog revision={typeof create === 'number' ? create : undefined} scene={scene}
      seed={create === 'pricelist' ? offers[2] : offers[0]} onClose={() => setCreate(null)} onCreate={created => {
        setRows(previous => [created, ...previous.filter(row => row.id !== created.id)]);
        open(created, typeof create === 'number');
        setCreate(null);
      }} />}
    {history && <Modal open nothingToLose title={`${history.number} · версия ${history.version}`} onOpenChange={next => { if (!next) setHistory(null); }}>
      <div className="grid gap-2"><OfferStatusText status={history.status} />
        <p className="text-body-medium">Отправлено {history.sentOn} · покрытие {history.coverage}.</p>
        <p className="text-body-medium text-on-surface-variant">Сохранённая версия · только чтение.</p></div>
    </Modal>}
  </div>;
}

function paramFrom<T extends string>(params: URLSearchParams, name: string, choices: Record<T, unknown>, fallback: T): T {
  const value = params.get(name);
  return value && Object.hasOwn(choices, value) ? value as T : fallback;
}

export function PlanOffers({ params }: { params: URLSearchParams }) {
  const variant = paramFrom(params, 'page', variantNotes, 'page');
  const [persona, setPersona] = useState<Persona>(paramFrom(params, 'role', personas, 'manager'));
  const [scene, setScene] = useState<Scene>(paramFrom(params, 'scene', scenes, 'normal'));
  const initialView = paramFrom<View>(params, 'view', { offers: true, pricing: true, estimate: true }, 'offers');
  return <div className="mx-auto grid max-w-[96rem] gap-3 p-3 sm:p-5">
    <header className="grid gap-3 rounded-lg bg-surface-container-low p-3 sm:p-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="max-w-2xl"><h1 className="text-title-large">{planOffersMockup.pages.find(page => page.path === variant)?.title}</h1>
          <p className="text-body-medium text-on-surface-variant">{variantNotes[variant]}</p></div>
        <div className="grid w-full gap-2 sm:w-auto sm:grid-cols-2">
          <Select label="Права в макете" density="dense" value={persona} onValueChange={value => setPersona(value as Persona)}>
            {Object.entries(personas).map(([key, label]) => <SelectItem key={key} value={key}>{label}</SelectItem>)}
          </Select>
          <Select label="Сценарий" density="dense" value={scene} onValueChange={value => setScene(value as Scene)}>
            {Object.entries(scenes).map(([key, label]) => <SelectItem key={key} value={key}>{label}</SelectItem>)}
          </Select>
        </div>
      </div>
    </header>
    <Workspace key={`${persona}/${scene}`} variant={variant} persona={persona} scene={scene} initialView={initialView} initialOffer={params.get('offer')} />
  </div>;
}
