import { Link, Route, Routes, useParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import { useConstructionDirectory } from '@/shared/api/constructions';
import { offerRefusal, useOffers } from '@/shared/api/planningOffers';
import type { OfferListItem } from '@/shared/api/planningOfferTypes';
import { formatDate } from '@/shared/format/format';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Description } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { OfferStatusText } from './offerDisplay';
import { moneyText, OFFERS_PATH, offerUrl, sourceLabel } from './offerFormat';
import { OfferCard } from './OfferCard';
import { PriceListOfferAction } from '../pricelist/PriceListOfferAction';
import { offersTableLink } from '../planTableLinks';

export function OffersPage() {
  return <div className="h-full overflow-auto">
    <div className="mx-auto max-w-7xl px-4 py-5 sm:px-6">
      <Routes>
        <Route index element={<OfferList />} />
        <Route path=":offerId" element={<OpenOffer />} />
        <Route path="*" element={<EmptyState icon={<Icon icon={Description} size={40} />} title="Страница КП не найдена"
          action={<Link className="text-primary underline" to={OFFERS_PATH}>К списку КП</Link>} />} />
      </Routes>
    </div>
  </div>;
}

function OpenOffer() {
  const { offerId } = useParams<{ offerId: string }>();
  return <OfferCard key={offerId} id={offerId!} />;
}

function OfferList() {
  const query = useOffers();
  const can = useCan();
  const canReadSites = can.permission('core.constructions.read');
  const sites = useConstructionDirectory(canReadSites);
  const filter = useSiteFilter();
  const table = offersTableLink(canReadSites && filter.active
    ? [...filter.favorites] : []);
  const siteNames = new Map(sites.data?.map(site => [site.id, site.name]));
  const loaded = query.data?.pages.flatMap(page => page.items) ?? [];
  const rows = canReadSites ? loaded.filter(row => filter.visible(row.constructionId)) : loaded;
  const columns: DataGridColumn<OfferListItem>[] = [
    { key: 'number', label: 'Номер', cell: row => row.number, rowHeader: true },
    ...(canReadSites ? [{ key: 'site', label: 'Стройка', wrap: true, cell: (row: OfferListItem) => siteNames.get(row.constructionId) ?? '—' }] : []),
    { key: 'source', label: 'Источник', cell: sourceLabel },
    { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
    { key: 'sentOn', label: 'Отправлено', cell: row => row.sentOn ? formatDate(row.sentOn) : '—' },
  ];
  if (rows.some(row => row.money != null)) columns.push({
    key: 'total', label: 'Итого', align: 'right', cell: row => <>{moneyText(row.money?.total)}
      {row.money?.incomplete && <span className="block text-body-small text-on-surface-variant">Неполный итог</span>}</>,
  });

  const refusal = query.isError ? offerRefusal(query.error, 'Коммерческие предложения не прочитались.') : null;
  return <section className="grid min-w-0 grid-cols-1 gap-4">
    <PageHeader icon={<Icon icon={Description} size={18} aria-hidden />} title="Коммерческие предложения"
      actions={<div className="flex flex-wrap items-center gap-2">
        {can.permission('plan.offer.read') && filter.status !== 'loading' && <Link
          className="text-body-medium text-primary hover:underline" to={table}>Открыть таблицей</Link>}
        <PriceListOfferAction />
      </div>} />
    {refusal && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>{refusal.message}</Banner>}
    {refusal?.kind !== 'forbidden' && (query.isPending || canReadSites && filter.status === 'loading' ? (
      <p role="status" className="text-body-medium text-on-surface-variant">Читаем коммерческие предложения…</p>
    ) : query.data && (loaded.length || query.hasNextPage ? <>
      {canReadSites && <SitesFilter hidden={loaded.length - rows.length} partial={query.hasNextPage} />}
      <DataGrid columns={columns} rows={rows} rowKey={row => row.id}
        state={rows.length < loaded.length ? 'filtered-out' : undefined}
        rowLink={row => ({ to: offerUrl(row.id), label: `Открыть ${row.number}` })} />
      {query.hasNextPage && <div><Button variant="outlined" disabled={query.isFetchingNextPage}
        onClick={() => void query.fetchNextPage()}>{query.isFetchingNextPage ? 'Читаем…' : 'Показать ещё'}</Button></div>}
    </> : <EmptyState icon={<Icon icon={Description} size={40} />} title="Коммерческих предложений пока нет"
      description="КП можно создать из редакции сметы или по прайс-листу." />))}
  </section>;
}
