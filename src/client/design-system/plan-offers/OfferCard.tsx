import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import { NumberInput } from '@/shared/ui/NumberInput';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { estimateLines, offerSource, priceLines, statusLabels, type Line, type Offer, type Persona, type Scene, type Variant } from './sample';

export type OfferAction = 'send' | 'outcome' | 'next' | 'rebase' | 'print';

export function OfferStatusText({ status }: { status: Offer['status'] }) {
  return <StatusText tone={status === 'accepted' ? 'success' : status === 'rejected' ? 'error' : status === 'draft' ? 'neutral' : 'primary'}>
    {statusLabels[status]}
  </StatusText>;
}

function LineTable({ offer, withCost }: { offer: Offer; withCost: boolean }) {
  const rows = offer.source === 'estimate' ? estimateLines : priceLines;
  const columns: DataGridColumn<Line>[] = [
    { key: 'name', label: 'Позиция', rowHeader: true, wrap: true, cell: row => <>
      {row.name}{row.noPrice && <span className="block"><StatusText tone="warning">Нет цены в прайс-листе</StatusText></span>}
    </> },
    { key: 'volume', label: 'Объём', align: 'right', cell: row => row.quantity },
    { key: 'markup', label: 'Действующая наценка', cell: row => `${row.markup} · ${row.level}` },
    { key: 'price', label: 'Цена продажи', align: 'right', cell: row => row.price },
    { key: 'amount', label: 'Сумма', align: 'right', cell: row => row.amount },
  ];
  if (withCost && offer.source === 'estimate') columns.push(
    { key: 'cost', label: 'Себестоимость', align: 'right', cell: row => row.cost },
    { key: 'margin', label: 'Маржа', align: 'right', cell: row => row.margin },
  );
  return <DataGrid columns={columns} rows={rows} rowKey={row => row.id} density="compact" pinned={1} />;
}

function Money({ offer }: { offer: Offer }) {
  return <dl className="grid gap-1 rounded-md bg-surface-container-low p-4 text-body-medium tabular-nums">
    <div className="flex justify-between gap-4"><dt>Сумма строк</dt><dd>{offer.net}</dd></div>
    <div className="flex justify-between gap-4"><dt>{offer.vat ? 'НДС 20 % сверху' : 'Без НДС'}</dt><dd>{offer.vat}</dd></div>
    <div className="flex flex-wrap justify-between gap-x-4 text-title-medium"><dt>{offer.incomplete ? 'Неполный итог' : 'Итого'}</dt><dd>{offer.total}</dd></div>
  </dl>;
}

const scopes = [
  { id: 'chapter-1', label: '01 Электроснабжение', level: 'Глава', own: '20', effective: '20 % · Глава' },
  { id: 'chapter-2', label: '02 Освещение', level: 'Глава', own: '', effective: '10 % · КП' },
  ...estimateLines.map(row => ({ id: row.id, label: row.name, level: 'Строка', own: row.level === 'Строка' ? '25' : '', effective: `${row.markup} · ${row.level}` })),
];

function Markups({ offer, editable, scene, onClose }: { offer: Offer; editable: boolean; scene: Scene; onClose?: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<'idle' | 'error' | 'reviewed' | 'saved'>('idle');
  const change = (id: string, value: string) => { setValues(previous => ({ ...previous, [id]: value })); setResult('idle'); };
  const general = offer.source === 'estimate' ? '10' : '0';
  const scopeRows = offer.source === 'estimate' ? scopes : priceLines.map(row => ({ id: row.id, label: row.name, level: 'Строка', own: '', effective: `${row.markup} · ${row.level}` }));
  const columns: EditGridColumn<(typeof scopes)[number]>[] = [
    { key: 'scope', label: 'Позиция', rowHeader: true, cell: row => <>{row.label}<small className="block text-on-surface-variant">{row.level}</small></> },
    { key: 'own', label: 'Своя, %', width: 'w-28', cell: row => editable ?
      <NumberInput label={`Своя наценка: ${row.label}`} value={values[row.id] ?? row.own} onChange={value => change(row.id, value)} density="cell" /> : row.own || '—' },
    { key: 'effective', label: 'Действует', cell: row => row.effective },
  ];
  const failure = scene === 'stale-offer' ? 'КП тем временем изменили. Перечитайте, затем проверьте введённые наценки.'
    : scene === 'forbidden' ? 'У вас больше нет права изменять КП. Введённые значения сохранены в форме.'
    : 'Сервер отклонил наценку. Проверьте значение. Введённые данные остались в форме.';
  return <section className="grid min-w-0 grid-cols-1 gap-3" aria-label="Наценки">
    {editable ? <div className="grid max-w-48 gap-1">
      <span className="text-label-large" aria-hidden="true">Наценка КП</span>
      <NumberInput label="Наценка КП, %" unit="%" value={values.offer ?? general} onChange={value => change('offer', value)} />
    </div>
      : <p className="text-body-medium">Наценка КП · {general} %</p>}
    <EditGrid columns={columns} rows={scopeRows} rowKey={row => row.id} label="Наценки глав и строк" />
    {editable && <>
      <p className="text-body-small text-on-surface-variant">Пустое поле наследует уровень выше. Действующие значения показаны по последнему ответу сервера.</p>
      {result === 'error' && <Banner tone="error" actions={scene === 'stale-offer' ? [{ label: 'Перечитать', onClick: () => setResult('reviewed') }] : undefined}>{failure}</Banner>}
      {result === 'reviewed' && <Banner tone="info">КП перечитано. Сверьте свои значения и сохраните ещё раз.</Banner>}
      {result === 'saved' && <Banner tone="success">Наценки сохранены.</Banner>}
      <div className="flex flex-wrap gap-2">
        <Button variant="filled" disabled={Object.keys(values).length === 0 || result === 'error' && scene === 'stale-offer'}
          onClick={() => setResult(scene === 'normal' || scene === 'stale-preview' || result === 'reviewed' ? 'saved' : 'error')}>Сохранить наценки</Button>
        {onClose && <Button variant="text" onClick={onClose}>Закрыть</Button>}
      </div>
    </>}
  </section>;
}

function Versions({ offer }: { offer: Offer }) {
  const rows = [{ version: offer.version, status: offer.status, sentOn: offer.sentOn, source: offerSource(offer) },
    ...(offer.version > 1 && offer.source === 'estimate' ? [{ version: 1, status: 'sent' as const, sentOn: '18.11.2026', source: 'Смета · ред. 2' }] : [])];
  return <DataGrid density="compact" rows={rows} columns={[
    { key: 'version', label: 'Версия', rowHeader: true, cell: row => row.version },
    { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
    { key: 'date', label: 'Отправлено', cell: row => row.sentOn ?? '—' },
    { key: 'source', label: 'Источник', cell: row => row.source },
  ]} />;
}

export function OfferCard({ offer, variant, persona, scene, onAction }: {
  offer: Offer; variant: Variant; persona: Persona; scene: Scene; onAction: (action: OfferAction) => void;
}) {
  const [panel, setPanel] = useState<'lines' | 'markups' | 'versions'>('lines');
  const [sheet, setSheet] = useState<'markups' | 'versions' | null>(null);
  const allowEdit = persona === 'manager' || persona === 'cost' || persona === 'editor';
  const allowSend = persona === 'manager' || persona === 'cost';
  const draft = offer.status === 'draft';
  const split = variant === 'split';
  const sectioned = variant === 'sections';
  const withCost = persona === 'cost';
  const lines = <section className="grid min-w-0 grid-cols-1 gap-3" aria-label="Строки КП">
    {offer.source === 'estimate' && !withCost && <p className="text-body-small text-on-surface-variant">Себестоимость и маржа скрыты. Нужно право «Видеть себестоимость сметы» (<code>plan.estimate.cost</code>).</p>}
    <LineTable offer={offer} withCost={withCost} />
  </section>;
  const markups = <Markups offer={offer} editable={allowEdit && draft} scene={scene} onClose={split ? () => setSheet(null) : undefined} />;
  return <article className="grid min-w-0 grid-cols-1 gap-4" aria-label={`Карточка ${offer.number}`}>
    <div className="flex flex-wrap justify-between gap-3">
      <div className="space-y-1">
        <h2 className="text-headline-small">{offer.number}</h2>
        <p className="text-body-medium text-on-surface-variant">{offer.site} · {offer.customer}</p>
        <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-body-medium"><OfferStatusText status={offer.status} />
          <span>{offerSource(offer)} · Версия {offer.version}</span>{offer.sentOn && <span>Отправлено {offer.sentOn}</span>}</p>
      </div>
      <div className="flex flex-wrap items-start gap-2">
        <Button variant="outlined" onClick={() => onAction('print')}>Печать…</Button>
        {allowSend && draft && <Button variant="filled" onClick={() => onAction('send')}>Отметить отправленным…</Button>}
        {allowSend && offer.status === 'sent' && <Button variant="outlined" onClick={() => onAction('outcome')}>Записать итог…</Button>}
        {allowEdit && !draft && <Button variant="filled" onClick={() => onAction('next')}>Новая версия…</Button>}
      </div>
    </div>
    {offer.revision && offer.latest && offer.revision !== offer.latest && <Banner tone="warning" actions={allowEdit ? [
      { label: 'Обновить по смете…', onClick: () => onAction('rebase') },
    ] : undefined}>Смета изменилась. КП по ред. {offer.revision}, действующая и последняя — ред. {offer.latest}.</Banner>}
    {offer.incomplete && <Banner tone="warning">Итог неполный. В КП есть строка без цены на дату предложения.</Banner>}
    {sectioned ? <>
      <Tabs id="plan-offer-card" aria-label="Карточка КП" value={panel} onChange={setPanel} items={[
        { value: 'lines', label: 'Строки и итог' }, { value: 'markups', label: 'Наценки' }, { value: 'versions', label: 'Версии' },
      ]} />
      <TabPanel id="plan-offer-card" value={panel}>
        {panel === 'lines' ? <div className="grid grid-cols-1 gap-3">{lines}<Money offer={offer} /></div> : panel === 'markups' ? markups : <Versions offer={offer} />}
      </TabPanel>
    </> : <>
      {split && <div className="flex flex-wrap gap-2"><Button variant="tonal" onClick={() => setSheet('markups')}>Наценки…</Button>
        <Button variant="text" onClick={() => setSheet('versions')}>Версии…</Button></div>}
      {lines}<Money offer={offer} />
      {split ? <SideSheet open={sheet !== null} variant="modal" title={sheet === 'markups' ? 'Наценки' : 'Версии КП'} onClose={() => setSheet(null)}>
        {sheet === 'markups' ? markups : <Versions offer={offer} />}
      </SideSheet> : <>
        <h3 className="text-title-medium">Наценки</h3>{markups}
        <h3 className="text-title-medium">Версии КП</h3><Versions offer={offer} />
      </>}
    </>}
  </article>;
}
