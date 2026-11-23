import { useState } from 'react';
import { offerRefusal, usePriceListReprice, type OfferRefusal } from '@/shared/api/planningOffers';
import type { Offer, PriceListRepricePreview } from '@/shared/api/planningOfferTypes';
import { formatDate } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid } from '@/shared/ui/DataGrid';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { OfferTotals } from '../offers/offerDisplay';
import { moneyText } from '../offers/offerFormat';
import type { ReloadOffer } from '../offers/useOfferWriteForm';

export function PriceListRepriceDialog({ offer, allowed, reload, onClose }: {
  offer: Offer; allowed: boolean; reload: ReloadOffer; onClose: () => void;
}) {
  const [seen, setSeen] = useState(offer);
  const [date, setDate] = useState(offer.date);
  const [incomplete, setIncomplete] = useState(false);
  const [preview, setPreview] = useState<PriceListRepricePreview | null>(null);
  const [refusal, setRefusal] = useState<OfferRefusal | null>(null);
  const [reloading, setReloading] = useState(false);
  const mutation = usePriceListReprice(offer.id);
  const forbidden = !allowed || refusal?.kind === 'forbidden';
  const blocked = seen.version !== offer.version || refusal?.kind === 'conflict';
  const draft = seen.status === 'draft';
  const busy = mutation.isPending || reloading;
  const valid = !forbidden && !blocked && draft && !!date && !incomplete;

  async function submit() {
    if (!valid || busy) return;
    try {
      const result = await mutation.mutateAsync({ version: seen.version, date, previewToken: preview?.previewToken ?? undefined });
      if (result.offer) onClose();
      else setPreview(result);
      setRefusal(null);
    } catch (error) {
      setPreview(null);
      setRefusal(offerRefusal(error, 'КП не пересчитано.'));
    }
  }

  async function refresh() {
    setReloading(true);
    try {
      setSeen(await reload());
      setPreview(null);
      setRefusal(null);
    } catch (error) {
      const failure = offerRefusal(error, 'КП не прочиталось.');
      setRefusal(failure.kind === 'forbidden' ? failure : { kind: 'conflict', message: failure.message });
    } finally {
      setReloading(false);
    }
  }

  return <Modal open wide title="Пересчитать по прайс-листу" busy={busy} nothingToLose onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit>Отмена</ModalExit><ModalPrimary disabled={!valid}
      onClick={() => void submit()}>{preview ? 'Подтвердить пересчёт' : 'Показать изменения'}</ModalPrimary></>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      {seen.priceListDate && <p className="text-body-medium">Сейчас в КП цены на {formatDate(seen.priceListDate)}.</p>}
      <DateField label="Цены на дату" value={date} required disabled={busy || forbidden || !draft}
        onChange={value => { setDate(value); setPreview(null); }} onIncomplete={message => {
          setIncomplete(Boolean(message));
          if (message) setPreview(null);
        }} />
      {!draft && <Banner tone="info">КП уже отправлено. Пересчёт доступен в новой версии.</Banner>}
      {forbidden && <Banner tone="error">Для пересчёта нужно право читать и править КП.</Banner>}
      {!forbidden && (refusal || blocked) && <Banner tone="error" actions={blocked ? [
        { label: 'Перечитать КП', onClick: () => void refresh(), loading: reloading },
      ] : undefined}>{refusal?.message ?? 'КП тем временем изменилось. Перечитайте его перед предпросмотром.'}</Banner>}
      {preview && !blocked && !forbidden && <>
        {preview.changes.length ? <DataGrid rows={preview.changes} rowKey={row => row.lineId} density="compact" columns={[
          { key: 'name', label: 'Вид работы', rowHeader: true, wrap: true, cell: row => row.name },
          { key: 'previousPrice', label: 'Прежняя цена', align: 'right', cell: row => moneyText(row.previousPrice) },
          { key: 'newPrice', label: 'Новая цена', align: 'right', cell: row =>
            row.priceSource.kind === 'none' ? 'Нет цены в прайс-листе' : moneyText(row.price) },
          { key: 'previousAmount', label: 'Прежняя сумма', align: 'right', cell: row => moneyText(row.previousAmount) },
          { key: 'newAmount', label: 'Новая сумма', align: 'right', cell: row => row.priceSource.kind === 'none' ? '—' : moneyText(row.amount) },
        ]} /> : <p className="text-body-medium">Изменений в строках нет.</p>}
        <OfferTotals money={preview.money} vat={seen.vat} />
      </>}
    </div>
  </Modal>;
}
