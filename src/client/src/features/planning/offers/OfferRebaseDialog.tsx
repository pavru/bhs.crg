import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { offerRefusal, readOfferPreview, useWriteOffer, type OfferRefusal } from '@/shared/api/planningOffers';
import type { Offer, OfferRebaseLine, OfferRebasePreview } from '@/shared/api/planningOfferTypes';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { markupText, moneyText } from './offerFormat';
import type { ReloadOffer } from './useOfferWriteForm';

export function OfferRebaseDialog({ offer, allowed, showCost, reload, onClose }: {
  offer: Offer; allowed: boolean; showCost: boolean; reload: ReloadOffer; onClose: () => void;
}) {
  const [seen, setSeen] = useState(offer);
  const [selected, setSelected] = useState<string | null>(null);
  const [replacement, setReplacement] = useState<OfferRebasePreview | null>(null);
  const [refusal, setRefusal] = useState<OfferRefusal | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [reloading, setReloading] = useState(false);
  const targets = [seen.drift?.current, seen.drift?.latest, replacement?.target].filter((item, index, all) =>
    item && item.id !== seen.revision?.id && all.findIndex(candidate => candidate?.id === item.id) === index);
  // Сервер без явной цели берёт действующую редакцию. Она может совпадать с источником КП.
  const revisionId = targets.find(item => item?.id === selected)?.id ?? targets[0]?.id;
  const query = useQuery({
    queryKey: ['planning', 'offer-rebase', offer.id, seen.version, revisionId],
    queryFn: ({ signal }) => readOfferPreview(offer.id, revisionId, signal),
    enabled: allowed && !!revisionId, gcTime: 0, retry: false, refetchOnWindowFocus: false,
  });
  const mutation = useWriteOffer(offer.id);
  const preview = replacement ?? query.data;
  const versionChanged = offer.version !== seen.version || (preview != null && preview.version !== seen.version);
  const conflict = refusal?.kind === 'conflict' || versionChanged;
  const queryRefusal = query.isError ? offerRefusal(query.error, 'Разница по смете не прочиталась.') : null;
  const forbidden = refusal?.kind === 'forbidden' || queryRefusal?.kind === 'forbidden' || !allowed;
  const valid = preview && preview.target.id !== preview.source.id && !forbidden && !conflict && !query.isFetching && !queryRefusal;
  const busy = mutation.isPending || reloading;

  async function confirm() {
    if (!valid || !preview || busy) return;
    try {
      await mutation.mutateAsync({ version: seen.version, command: { type: 'rebase', fingerprint: preview.fingerprint,
        revisionId: preview.target.id } });
      onClose();
    } catch (error) {
      const next = offerRefusal(error, 'КП не обновлено.');
      setRefusal(next);
      if (next.kind === 'preview-changed') setReplacement(next.preview);
    }
  }

  async function refresh() {
    setReloading(true);
    try {
      const fresh = await reload();
      if (fresh.version === seen.version && revisionId) await query.refetch({ throwOnError: true });
      setSeen(fresh);
      setReplacement(null);
      setRefusal(null);
      setReviewed(true);
    } catch (error) {
      const next = offerRefusal(error, 'Предпросмотр не перечитался.');
      setRefusal(next.kind === 'forbidden' ? next : { kind: 'conflict', message: next.message });
    } finally {
      setReloading(false);
    }
  }

  return <Modal open wide title="Обновить КП по смете" nothingToLose busy={busy} onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit>Отмена</ModalExit>{!forbidden && <ModalPrimary disabled={!valid} onClick={() => void confirm()}>
      {seen.status === 'draft' ? 'Обновить черновик' : 'Создать черновик по новой редакции'}
    </ModalPrimary>}</>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      {targets.length > 1 && !forbidden && <Select label="Целевая редакция" value={replacement?.target.id ?? revisionId ?? ''} disabled={busy}
        onValueChange={value => { setSelected(value); setReplacement(null); setRefusal(null); setReviewed(false); }}>
        {targets.map(item => item && <SelectItem key={item.id} value={item.id}>Редакция {item.number}</SelectItem>)}
      </Select>}
      {forbidden ? <Banner tone="error">Для предпросмотра и обновления нужны чтение и правка КП.</Banner>
        : <>
          {queryRefusal && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>{queryRefusal.message}</Banner>}
          {(refusal || versionChanged) && <Banner tone={!conflict && refusal?.kind === 'preview-changed' ? 'warning' : 'error'} actions={conflict ? [{
            label: 'Перечитать КП и разницу', onClick: () => void refresh(), loading: reloading,
          }] : undefined}>{versionChanged ? 'КП тем временем изменилось. Перечитайте его и проверьте разницу.' : refusal?.message}</Banner>}
          {reviewed && preview && !query.isFetching && !queryRefusal && !refusal && !versionChanged &&
            <Banner tone="info">КП и разница перечитаны. Проверьте изменения и подтвердите ещё раз.</Banner>}
          {!revisionId && <p className="text-body-medium">Других редакций для сравнения пока нет.</p>}
          {revisionId && query.isPending && <p role="status" className="text-body-medium">Сравниваем редакции…</p>}
          {preview && !versionChanged && <>
            <p className="text-body-medium">Редакция {preview.source.number} → редакция {preview.target.number}. {seen.status === 'draft'
              ? 'Обновится текущий черновик.' : 'Отправленная версия останется в истории. Откроется новый черновик.'}</p>
            <RebaseDifference preview={preview} showCost={showCost} />
          </>}
        </>}
    </div>
  </Modal>;
}

type Change = { key: string; kind: string; before: OfferRebaseLine[]; after: OfferRebaseLine[] };

function RebaseDifference({ preview, showCost }: { preview: OfferRebasePreview; showCost: boolean }) {
  const mayShowCost = showCost && !preview.hidden?.some(part => part.code === 'cost');
  const rows: Change[] = [
    ...preview.added.map(row => ({ key: `added:${row.revisionRowId}`, kind: 'Добавлена', before: [], after: [row] })),
    ...preview.removed.map(row => ({ key: `removed:${row.revisionRowId}`, kind: 'Снята', before: [row], after: [] })),
    ...preview.changed.map(({ before, after }) => ({ key: `changed:${before.revisionRowId}`, kind: 'Изменена', before: [before], after: [after] })),
    ...preview.unmatched.map(({ before, after }) => ({ key: `unmatched:${before.map(row => row.revisionRowId).join(',')}`,
      kind: 'Неоднозначно', before, after })),
  ];
  const columns: DataGridColumn<Change>[] = [
    { key: 'kind', label: 'Изменение', rowHeader: true, wrap: true,
      cell: row => <StatusText tone={row.kind === 'Неоднозначно' ? 'warning' : 'neutral'}>{row.kind}</StatusText> },
    { key: 'before', label: 'Было', wrap: true, cell: row => <DifferenceLines rows={row.before} showCost={mayShowCost} /> },
    { key: 'after', label: 'Станет', wrap: true, cell: row => <DifferenceLines rows={row.after} showCost={mayShowCost} /> },
  ];
  const hidden = preview.hidden?.find(part => part.code === 'cost');
  return <>
    {hidden && <p className="text-body-small text-on-surface-variant">Себестоимость и маржа скрыты. Нужно право «{hidden.name}».</p>}
    {rows.length ? <DataGrid rows={rows} columns={columns} rowKey={row => row.key} density="compact" />
      : <p className="text-body-medium">Изменений в строках нет.</p>}
    {preview.unmatched.length > 0 && <Banner tone="warning">При неоднозначном сопоставлении свои наценки строк не переносятся.</Banner>}
    {preview.unmatchedSectionMarkups.length > 0 && <div className="space-y-2"><Banner tone="warning">Не найдены однозначные главы для переноса наценок.</Banner>
      <ul className="list-disc ps-5 text-body-medium">{preview.unmatchedSectionMarkups.map(({ source, markup, candidates }) => <li key={source.code ?? source.name}>
        {source.name ?? source.code} · {markupText(markup)}{candidates.length > 0 && ` · кандидаты ${candidates.map(item => item.name ?? item.code).join(', ')}`}
      </li>)}</ul>
    </div>}
  </>;
}

function DifferenceLines({ rows, showCost }: { rows: OfferRebaseLine[]; showCost: boolean }) {
  if (!rows.length) return '—';
  return <ul className="space-y-2">{rows.map(row => <li key={row.revisionRowId}>
    <span className="block font-medium">{[row.number, row.name].filter(Boolean).join(' · ')}</span>
    {row.lsrSection?.name && <span className="block text-body-small text-on-surface-variant">{row.lsrSection.name}</span>}
    <span className="block text-body-small">{row.volume == null ? '—' : formatNumber(row.volume, 6)} {row.unit} · цена {moneyText(row.price)} · сумма {moneyText(row.amount)}</span>
    {row.ownMarkup !== null && <span className="block text-body-small">Своя наценка {markupText(row.ownMarkup)}</span>}
    {showCost && row.cost && <span className="block text-body-small">Себестоимость {moneyText(row.cost.total)} · маржа {moneyText(row.cost.margin)}</span>}
  </li>)}</ul>;
}
