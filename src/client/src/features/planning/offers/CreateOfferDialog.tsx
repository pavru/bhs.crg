import { useState } from 'react';
import { Link } from 'react-router';
import { offerRefusal, useCreateOffer } from '@/shared/api/planningOffers';
import type { Offer, OfferVatMode } from '@/shared/api/planningOfferTypes';
import { companyToday } from '@/shared/format/companyDay';
import { useLocale } from '@/shared/hooks/useLocale';
import { parseNumber } from '@/shared/utils/parseNumber';
import { Banner } from '@/shared/ui/Banner';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberField, OfferHeaderFields } from './OfferHeaderFields';
import { offerUrl } from './offerFormat';

export function CreateOfferDialog({ estimateName, revision, canOpen, onClose, onCreated }: {
  estimateName: string; revision: { id: string; number: number }; canOpen: boolean;
  onClose: () => void; onCreated: (offer: Offer) => void;
}) {
  const [locale] = useLocale();
  const [today] = useState(companyToday);
  const [date, setDate] = useState(today);
  const [dateIncomplete, setDateIncomplete] = useState(false);
  const [markup, setMarkup] = useState('0');
  const [mode, setMode] = useState<OfferVatMode>('none');
  const [rate, setRate] = useState('');
  const [created, setCreated] = useState<Offer | null>(null);
  const mutation = useCreateOffer();
  const markupValue = parseNumber(markup, locale);
  const vatRate = mode === 'none' ? null : parseNumber(rate, locale);
  const valid = date && !dateIncomplete && markupValue !== null && (mode === 'none' || vatRate !== null);

  async function create() {
    if (!valid || markupValue === null || mutation.isPending) return;
    try {
      const offer = await mutation.mutateAsync({ revisionId: revision.id, date, markup: markupValue, vatMode: mode, vatRate });
      setCreated(offer);
      if (canOpen) onCreated(offer);
    } catch { /* Отказ остаётся в открытой форме. */ }
  }

  if (created) return <Modal open acknowledge="Готово" title="КП создано" onOpenChange={open => { if (!open) onClose(); }}>
    <p className="text-body-medium">Создан черновик {created.number}.</p>
    {canOpen && <Link className="text-primary underline" to={offerUrl(created.id)}>Открыть КП</Link>}
  </Modal>;

  return <Modal open title={`Сделать КП из редакции ${revision.number}`} onOpenChange={open => { if (!open) onClose(); }}
    isDirty={date !== today || markup !== '0' || mode !== 'none' || rate !== '' || dateIncomplete}
    busy={mutation.isPending} footer={<><ModalExit>Отмена</ModalExit>
      <ModalPrimary disabled={!valid} onClick={() => void create()}>Создать черновик</ModalPrimary></>}>
    <div className="grid gap-3">
      <p className="text-body-medium text-on-surface-variant">{estimateName}</p>
      <OfferHeaderFields date={date} setDate={setDate} onDateIncomplete={setDateIncomplete} mode={mode} setMode={setMode}
        rate={rate} setRate={setRate} disabled={mutation.isPending} />
      <NumberField label="Наценка КП" value={markup} onChange={setMarkup} disabled={mutation.isPending} />
      {mutation.isError && <Banner tone="error">{offerRefusal(mutation.error, 'КП не создано.').message}</Banner>}
    </div>
  </Modal>;
}
