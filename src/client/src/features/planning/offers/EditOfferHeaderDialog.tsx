import { useState } from 'react';
import type { Offer, OfferVatMode } from '@/shared/api/planningOfferTypes';
import { formatInput } from '@/shared/format/format';
import { useLocale } from '@/shared/hooks/useLocale';
import { Banner } from '@/shared/ui/Banner';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { parseNumber } from '@/shared/utils/parseNumber';
import { OfferHeaderFields } from './OfferHeaderFields';
import { OfferWriteNotice } from './OfferWriteNotice';
import { useOfferWriteForm, type ReloadOffer } from './useOfferWriteForm';

export function EditOfferHeaderDialog({ offer, canEdit, reload, onClose }: {
  offer: Offer; canEdit: boolean; reload: ReloadOffer; onClose: () => void;
}) {
  const [opened] = useState(offer);
  const [changes, setChanges] = useState<{ date?: string; mode?: OfferVatMode; rate?: string }>({});
  const [incomplete, setIncomplete] = useState(false);
  const [locale] = useLocale();
  const form = useOfferWriteForm(opened, reload);
  const { seen } = form;
  const date = changes.date ?? seen.date;
  const mode = changes.mode ?? seen.vat.mode;
  const rate = changes.rate ?? formatInput(seen.vat.rate);
  const vatRate = mode === 'none' ? null : parseNumber(rate, locale);
  const allowed = canEdit && offer.status === 'draft' && seen.status === 'draft';
  const dirty = Object.keys(changes).length > 0 || incomplete;
  const valid = date && !incomplete && (mode === 'none' || vatRate !== null);

  return <Modal open title="Изменить дату и НДС" isDirty={dirty} busy={form.busy} onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit>Отмена</ModalExit><ModalPrimary disabled={!dirty || !valid || form.blocked || !allowed} onClick={async () => {
      if (allowed && valid && await form.apply({ type: 'header', input: { date, vatMode: mode, vatRate,
        customerId: seen.customer?.id ?? null, data: seen.data } })) onClose();
    }}>Сохранить</ModalPrimary></>}>
    <div className="grid gap-3">
      <OfferHeaderFields date={date} setDate={value => setChanges(previous => ({ ...previous, date: value }))}
        onDateIncomplete={setIncomplete} mode={mode} setMode={value => setChanges(previous => ({ ...previous, mode: value }))}
        rate={rate} setRate={value => setChanges(previous => ({ ...previous, rate: value }))} disabled={!allowed || form.busy} />
      {!allowed && <Banner tone="warning">Правка даты и НДС сейчас недоступна.</Banner>}
      <OfferWriteNotice form={form} hasInput={dirty} />
    </div>
  </Modal>;
}
