import { useId, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import { useConstructionDirectory } from '@/shared/api/constructions';
import { costHidden } from '@/shared/api/planningEstimates';
import { offerRefusal, useOffer } from '@/shared/api/planningOffers';
import type { Offer } from '@/shared/api/planningOfferTypes';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { PageHeader } from '@/shared/ui/PageHeader';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { EditOfferHeaderDialog } from './EditOfferHeaderDialog';
import { offerTableLink } from '../planTableLinks';
import { OfferDriftBanner } from './OfferDriftBanner';
import { OfferLines } from './OfferLines';
import { OfferMarkups } from './OfferMarkups';
import { OfferPrintDialog } from './OfferPrintDialog';
import { OfferRebaseDialog } from './OfferRebaseDialog';
import { OfferStatusActions, OfferStatusDialog, type StatusAction } from './OfferStatusActions';
import { OfferVersions } from './OfferVersions';
import { PriceListRepriceDialog } from '../pricelist/PriceListRepriceDialog';
import { OfferStatusText, OfferTotals } from './offerDisplay';
import { moneyText, OFFERS_PATH, sourceLabel } from './offerFormat';
import type { ReloadOffer } from './useOfferWriteForm';

type Tab = 'lines' | 'markups' | 'versions';
type Layer = StatusAction | 'header' | 'print' | 'rebase' | 'reprice';

export function OfferCard({ id }: { id: string }) {
  const query = useOffer(id);
  const guardedClick = useGuardedNavClick();
  const { state } = useLocation();
  const fromEstimate = typeof state?.estimatePath === 'string' && /^\/(estimates|document-sets)\//.test(state.estimatePath)
    ? state.estimatePath as string : null;
  const target = fromEstimate ?? OFFERS_PATH;
  const backState = fromEstimate && typeof state?.estimateRevisionId === 'string' ? { revisionId: state.estimateRevisionId } : undefined;
  const refusal = query.isError ? offerRefusal(query.error, 'КП не прочиталось.') : null;
  const data = refusal?.kind === 'forbidden' ? null : query.data;
  async function reload() {
    return (await query.refetch({ throwOnError: true })).data!;
  }
  return <div className="grid min-w-0 grid-cols-1 gap-4">
    <div><Link to={target} state={backState} onClick={guardedClick(target, { state: backState })}
      className="text-body-medium text-primary hover:underline">
      {fromEstimate ? 'К смете' : 'К списку КП'}
    </Link></div>
    {refusal && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>{refusal.message}</Banner>}
    {query.isPending && <p role="status" className="text-body-medium">Читаем КП…</p>}
    {data && <OfferDetail offer={data} reload={reload} />}
  </div>;
}

function OfferDetail({ offer, reload }: { offer: Offer; reload: ReloadOffer }) {
  const can = useCan();
  const guardedClick = useGuardedNavClick();
  const canRead = can.permission('plan.offer.read');
  const canEdit = can.permission('plan.offer.edit');
  const canSend = can.permission('plan.offer.send');
  const canReadCost = can.permission('plan.estimate.cost');
  const canReadSites = can.permission('core.constructions.read');
  const sites = useConstructionDirectory(canReadSites);
  const [tab, setTab] = useState<Tab>('lines');
  const [params, setParams] = useSearchParams();
  const { state: origin } = useLocation();
  const [layer, setLayer] = useState<Layer | null>(null);
  const [markupsDirty, setMarkupsDirty] = useState(false);
  const tabId = useId();
  const rawVersion = params.get('version');
  const selected = rawVersion && /^\d+$/.test(rawVersion) ? Number(rawVersion) : null;
  const historical = selected !== null && selected !== offer.currentVersion;
  const view = historical ? 'versions' : tab;
  const draft = offer.status === 'draft';
  const hidden = costHidden(offer.hidden);
  const showCost = canReadCost && hidden === null;
  const site = canReadSites ? sites.data?.find(row => row.id === offer.constructionId)?.name : null;
  const subtitle = [site, offer.customer?.name, formatDate(offer.date)].filter(Boolean).join(' · ');
  const close = () => setLayer(null);
  const statusAction = layer === 'send' || layer === 'outcome' || layer === 'next' ? layer : null;

  function showCurrent(value: Tab) {
    setTab(value);
    if (params.has('version')) setParams(previous => {
      const next = new URLSearchParams(previous);
      next.delete('version');
      return next;
    }, { replace: true, state: origin });
  }

  const showTab = historical ? showCurrent : setTab;
  const table = offerTableLink(offer.number);

  function openNewDraft() {
    close();
    showCurrent('lines');
  }

  return <article className="grid min-w-0 grid-cols-1 gap-4" aria-label={`Карточка ${offer.number}`}>
    <PageHeader title={offer.number} subtitle={historical ? site ?? undefined : subtitle}
      actions={!historical && <div className="flex flex-wrap gap-2">
        {canRead && <Link className="self-center text-body-medium text-primary hover:underline"
          to={table} onClick={guardedClick(table)}>Открыть таблицей</Link>}
        {canRead && <Button variant="outlined" disabled={markupsDirty} onClick={() => setLayer('print')}>Печать…</Button>}
        <OfferStatusActions offer={offer} canEdit={canEdit} canSend={canSend} disabled={markupsDirty} onOpen={setLayer} />
      </div>} />
    {!historical && <>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-body-medium">
        <OfferStatusText status={offer.status} />
        <span>Версия {offer.currentVersion} · {sourceLabel(offer)}</span>
        {offer.sentOn && <span>Отправлено {formatDate(offer.sentOn)}</span>}
      </div>
      {offer.drift && <OfferDriftBanner drift={offer.drift} onOpen={canRead && canEdit && !markupsDirty ? () => setLayer('rebase') : undefined} />}
      {markupsDirty && <Banner tone="info" actions={view !== 'markups' ? [{ label: 'К наценкам', onClick: () => showTab('markups') }] : undefined}>
        Сохраните или отмените правки наценок, чтобы печатать или изменять КП.
      </Banner>}
    </>}
    <Tabs id={tabId} variant="primary" aria-label="Карточка КП" value={view} onChange={showTab} items={[
      { value: 'lines', label: 'Строки и итог' }, { value: 'markups', label: 'Наценки' }, { value: 'versions', label: 'Версии' },
    ]} />
    <TabPanel id={tabId} value={view} className="min-w-0 space-y-3">
      {view === 'lines' && <div className="grid min-w-0 grid-cols-1 gap-3">
        {(hidden || offer.source === 'estimate' && !canReadCost) && <p className="text-body-small text-on-surface-variant">
          Себестоимость и маржа скрыты. Нужно право «{hidden?.name ?? 'видеть себестоимость сметы'}».
        </p>}
        {offer.money.incomplete && <Banner tone="warning">Итог неполный. В КП есть позиции без цены.</Banner>}
        <OfferLines lines={offer.lines} showCost={showCost} />
        <OfferTotals money={offer.money} vat={offer.vat} />
        {showCost && offer.cost && <p className="text-body-medium">Себестоимость {moneyText(offer.cost.total)} · маржа {moneyText(offer.cost.margin)}</p>}
        {draft && canEdit && !markupsDirty && <div className="flex flex-wrap gap-2">
          <Button variant="outlined" onClick={() => setLayer('header')}>Изменить дату и НДС…</Button>
          {canRead && offer.source === 'pricelist' && <Button variant="outlined" onClick={() => setLayer('reprice')}>Пересчитать по прайс-листу…</Button>}
        </div>}
      </div>}
      {(view === 'markups' || markupsDirty) && <div hidden={view !== 'markups'}>
        <OfferMarkups offer={offer} mayEdit={canEdit && !historical} reload={reload} onDirty={setMarkupsDirty} />
      </div>}
      {view === 'versions' && <OfferVersions offer={offer} selected={selected} onReturn={() => showTab('versions')} />}
    </TabPanel>
    {statusAction && <OfferStatusDialog offer={offer} action={statusAction} reload={reload}
      allowed={statusAction === 'next' ? canEdit : canSend} onClose={close} onSuccess={statusAction === 'next' ? openNewDraft : close} />}
    {layer === 'header' && <EditOfferHeaderDialog offer={offer} canEdit={canEdit} reload={reload} onClose={close} />}
    {layer === 'rebase' && <OfferRebaseDialog offer={offer} allowed={canRead && canEdit} showCost={showCost} reload={reload}
      onClose={close} onSuccess={openNewDraft} />}
    {layer === 'reprice' && <PriceListRepriceDialog offer={offer} allowed={canRead && canEdit} reload={reload} onClose={close} />}
    {layer === 'print' && canRead && <OfferPrintDialog offer={offer} onClose={close} />}
  </article>;
}
