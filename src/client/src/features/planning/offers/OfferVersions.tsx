import { Link } from 'react-router';
import { offerRefusal, useOfferVersions } from '@/shared/api/planningOffers';
import type { Offer, OfferVersion } from '@/shared/api/planningOfferTypes';
import { formatDate, formatDateTime } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { SnapshotLines } from './OfferLines';
import { OfferStatusText, OfferTotals } from './offerDisplay';
import { markupText, moneyText, offerUrl } from './offerFormat';

export function OfferVersions({ offer, selected }: { offer: Offer; selected: number | null }) {
  const query = useOfferVersions(offer.id, true);
  const guardedClick = useGuardedNavClick();
  if (query.isError) return <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
    {offerRefusal(query.error, 'Версии КП не прочитались.').message}
  </Banner>;
  if (!query.data) return <p role="status" className="text-body-medium">Читаем версии КП…</p>;

  const currentUrl = offerUrl(offer.id);
  if (selected !== null && selected !== offer.currentVersion) {
    const version = query.data.find(item => item.number === selected);
    return <section className="grid min-w-0 grid-cols-1 gap-3">
      <div><Link className="text-primary underline" to={currentUrl} onClick={guardedClick(currentUrl)}>К текущей версии</Link></div>
      {version ? <OldVersion version={version} /> : <Banner tone="warning">Версия {selected} не найдена.</Banner>}
    </section>;
  }

  const rows = [{ number: offer.currentVersion, status: offer.status, sentOn: offer.sentOn, total: offer.money.total, at: null },
    ...query.data.map(({ number, status, sentOn, total, at }) => ({ number, status, sentOn, total, at }))];
  const columns: DataGridColumn<(typeof rows)[number]>[] = [
    { key: 'version', label: 'Версия', rowHeader: true, cell: row => `Версия ${row.number}${row.at ? '' : ' · текущая'}` },
    { key: 'status', label: 'Статус', cell: row => <OfferStatusText status={row.status} /> },
    { key: 'sent', label: 'Отправлено', cell: row => row.sentOn ? formatDate(row.sentOn) : '—' },
    { key: 'total', label: 'Итого', align: 'right', cell: row => moneyText(row.total) },
    { key: 'saved', label: 'Сохранена в истории', cell: row => row.at ? formatDateTime(row.at) : '—' },
  ];
  return <DataGrid rows={rows} columns={columns} rowKey={row => String(row.number)} density="compact"
    rowLink={row => row.at ? { to: offerUrl(offer.id, row.number), label: `Открыть версию ${row.number}` } : null} />;
}

function OldVersion({ version }: { version: OfferVersion }) {
  const { content } = version;
  return <>
    <div className="space-y-1">
      <h2 className="text-title-large">Версия {version.number} · только чтение</h2>
      <div className="flex flex-wrap gap-x-3 gap-y-1 text-body-medium">
        <OfferStatusText status={version.status} />
        {version.sentOn && <span>Отправлено {formatDate(version.sentOn)}</span>}
        <span>Дата КП {formatDate(content.date)}</span>
      </div>
      <p className="text-body-small text-on-surface-variant">Сохранено {formatDateTime(version.at)}{content.authorName && ` · ${content.authorName}`}</p>
    </div>
    <SnapshotLines lines={content.lines} />
    <OfferTotals money={content.money} vat={content.vat} />
    <p className="text-body-medium">Наценка КП · {markupText(content.markup)}</p>
    {content.sections.length > 0 && <DataGrid rows={content.sections} rowKey={row => row.sectionCode} density="compact" columns={[
      { key: 'section', label: 'Глава', rowHeader: true, cell: row => row.sectionCode },
      { key: 'markup', label: 'Своя наценка', align: 'right', cell: row => markupText(row.markup) },
    ]} />}
  </>;
}
