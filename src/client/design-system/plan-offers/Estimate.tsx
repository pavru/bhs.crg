import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid } from '@/shared/ui/DataGrid';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { OfferStatusText } from './OfferCard';
import { estimateLines, revisions, type Offer, type Variant } from './sample';

export interface Derivative {
  id: string; number: string; version: number; status: Offer['status']; sentOn?: string; coverage: string;
  historical: boolean; outdated: boolean;
}

const examples: Record<number, Derivative[]> = {
  2: [{ id: 'main', number: 'КП-2026-0042', version: 1, status: 'sent', sentOn: '18.11.2026', coverage: '12 из 14 · 85,71 %', historical: true, outdated: true }],
  3: [{ id: 'main', number: 'КП-2026-0042', version: 2, status: 'draft', coverage: '16 из 18 · 88,89 %', historical: false, outdated: true },
    { id: 'secondary', number: 'КП-2026-0041', version: 1, status: 'sent', sentOn: '20.11.2026', coverage: '18 из 18 · 100 %', historical: false, outdated: true }],
  4: [],
};

function Derivatives({ revisions: numbers, canRead, onOffer }: {
  revisions: number[]; canRead: boolean; onOffer: (item: Derivative) => void;
}) {
  return <section className="grid grid-cols-1 gap-3" aria-label="Производные сметы">
    <h3 className="text-title-medium">Производные</h3>
    {!canRead ? <Banner tone="info">КП скрыты. Нужно право «Читать коммерческие предложения» (<code>plan.offer.read</code>).</Banner>
      : numbers.map(number => <section key={number} className="grid min-w-0 grid-cols-1 gap-2 rounded-md border border-outline-variant bg-surface p-3">
        <h4 className="text-title-small">Редакция {number}{number === 4 ? ' · Действующая' : ''}</h4>
        {examples[number].length === 0 ? <p className="text-body-medium text-on-surface-variant">КП по этой редакции пока нет.</p> : <DataGrid
          density="compact" framed={false} rows={examples[number]} rowKey={row => `${row.id}-${row.version}`} onRowOpen={onOffer} columns={[
            { key: 'offer', label: 'КП', rowHeader: true, cell: row => <>{row.number}<small className="block text-on-surface-variant">Версия {row.version}{row.historical ? ' · История' : ''}</small></> },
            { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
            { key: 'date', label: 'Отправлено', cell: row => row.sentOn ?? '—' },
            { key: 'coverage', label: 'Покрытие', cell: row => row.coverage },
            { key: 'outdated', label: 'Редакция', cell: row => row.outdated ? <StatusText tone="warning">Устарело</StatusText> : 'Действующая' },
          ]} />}
      </section>)}
    <p className="text-body-small text-on-surface-variant">Графики скрыты. Нужно право «Читать графики» (<code>plan.schedule.read</code>).</p>
  </section>;
}

export function Estimate({ variant, canRead, onOffer, onCreate }: {
  variant: Variant; canRead: boolean; onOffer: (item: Derivative) => void; onCreate?: (revision: number) => void;
}) {
  const [revision, setRevision] = useState('3');
  const [tab, setTab] = useState<'revisions' | 'attachments' | 'derivatives'>('revisions');
  const items = [{ value: 'revisions', label: 'Редакции' }, { value: 'attachments', label: 'Приложения' }] as { value: typeof tab; label: string }[];
  if (variant === 'split') items.push({ value: 'derivatives', label: 'Производные' });
  return <section className="grid min-w-0 grid-cols-1 gap-4">
    <PageHeader title="Электромонтажные работы" subtitle="Смета · РТК · Корпус 1" />
    <Tabs id="plan-estimate" aria-label="Карточка сметы" value={tab} onChange={setTab} items={items} />
    <TabPanel id="plan-estimate" value={tab}>
      {tab === 'derivatives' ? <Derivatives revisions={revisions} canRead={canRead} onOffer={onOffer} /> : tab === 'attachments'
        ? <p className="pt-3 text-body-medium text-on-surface-variant">У сметы нет приложений.</p> : <div className="grid gap-4">
          <Tabs id="plan-estimate-revisions" variant="underline" aria-label="Редакции сметы" value={revision} onChange={setRevision}
            items={revisions.map(number => ({ value: String(number), label: `Ред. ${number}${number === 4 ? ' · Действующая' : ''}` }))} />
          <TabPanel id="plan-estimate-revisions" value={revision}>
            <div className="grid grid-cols-1 gap-4">
              <div className="flex flex-wrap items-center justify-between gap-2"><h3 className="text-title-medium">Строки редакции {revision}</h3>
                {onCreate && <Button variant="filled" onClick={() => onCreate(Number(revision))}>Сделать КП…</Button>}</div>
              <DataGrid density="compact" rows={estimateLines} rowKey={row => row.id} columns={[
                { key: 'line', label: 'Позиция', rowHeader: true, wrap: true, cell: row => row.name },
                { key: 'section', label: 'Глава', wrap: true, cell: row => row.section },
                { key: 'quantity', label: 'Объём', align: 'right', cell: row => row.quantity },
              ]} />
              {variant === 'sections' && <Derivatives revisions={[Number(revision)]} canRead={canRead} onOffer={onOffer} />}
            </div>
          </TabPanel>
          {variant === 'page' && <Derivatives revisions={revisions} canRead={canRead} onOffer={onOffer} />}
        </div>}
    </TabPanel>
  </section>;
}
