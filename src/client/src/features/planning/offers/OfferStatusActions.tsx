import { useState } from 'react';
import type { Offer, OfferOutcome } from '@/shared/api/planningOfferTypes';
import type { OfferCommand } from '@/shared/api/planningOffers';
import { companyToday } from '@/shared/format/companyDay';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Banner } from '@/shared/ui/Banner';
import { STATUS_LABELS } from './offerFormat';
import { OfferWriteNotice } from './OfferWriteNotice';
import { useOfferWriteForm, type ReloadOffer } from './useOfferWriteForm';

export type StatusAction = 'send' | 'outcome' | 'next';
const titles: Record<StatusAction, string> = { send: 'Отметить КП отправленным', outcome: 'Записать итог КП', next: 'Новая версия КП' };
const outcomes = ['accepted', 'rejected', 'no-answer'] as const;

export function OfferStatusActions({ offer, canEdit, canSend, disabled, onOpen }: {
  offer: Offer; canEdit: boolean; canSend: boolean; disabled: boolean; onOpen: (action: StatusAction) => void;
}) {
  return <>
    {canSend && offer.status === 'draft' && <Button variant="filled" disabled={disabled} onClick={() => onOpen('send')}>Отметить отправленным…</Button>}
    {canSend && offer.status === 'sent' && <Button variant="outlined" disabled={disabled} onClick={() => onOpen('outcome')}>Записать итог…</Button>}
    {canEdit && offer.status !== 'draft' && <Button variant="filled" disabled={disabled} onClick={() => onOpen('next')}>Новая версия…</Button>}
  </>;
}

export function OfferStatusDialog({ offer, action, reload, allowed, onClose, onSuccess }: {
  offer: Offer; action: StatusAction; reload: ReloadOffer; allowed: boolean; onClose: () => void; onSuccess: () => void;
}) {
  const [opened] = useState(offer);
  const [today] = useState(companyToday);
  const [date, setDate] = useState(today);
  const [incomplete, setIncomplete] = useState(false);
  const [outcome, setOutcome] = useState<OfferOutcome | ''>('');
  const form = useOfferWriteForm(opened, reload);
  const status = form.seen.status;
  const usable = allowed && (action === 'send' ? status === 'draft' : action === 'outcome' ? status === 'sent' : status !== 'draft');
  const command: OfferCommand | null = action === 'send' ? date && !incomplete ? { type: 'send', sentOn: date } : null
    : action === 'outcome' ? outcome ? { type: 'outcome', status: outcome } : null : { type: 'next' };
  const dirty = date !== today || incomplete || outcome !== '';
  return <Modal open title={titles[action]} isDirty={dirty} busy={form.busy} onOpenChange={open => { if (!open) onClose(); }}
    footer={<><ModalExit>Отмена</ModalExit>
      <ModalPrimary disabled={!command || !usable || form.blocked} onClick={async () => {
        if (command && usable && await form.apply(command)) onSuccess();
      }}>{action === 'send' ? 'Отметить отправленным' : action === 'outcome' ? 'Записать итог' : 'Создать версию'}</ModalPrimary></>}>
    <div className="grid gap-3">
      {action === 'send' && <>
        <DateField label="Дата отправки" value={date} onChange={setDate} onIncomplete={value => setIncomplete(Boolean(value))} required disabled={!usable || form.busy} />
        <p className="text-body-medium">После отправки правка КП доступна только новой версией.</p>
      </>}
      {action === 'outcome' && <Select label="Итог" value={outcome} placeholder="Выберите итог" disabled={!usable || form.busy}
        onValueChange={value => setOutcome(value as OfferOutcome)}>
        {outcomes.map(value => <SelectItem key={value} value={value}>{STATUS_LABELS[value]}</SelectItem>)}
      </Select>}
      {action === 'next' && <p className="text-body-medium">Версия {form.seen.currentVersion} останется в истории. Откроется новый черновик.</p>}
      {!usable && <Banner tone="warning">Это действие сейчас недоступно.</Banner>}
      <OfferWriteNotice form={form} hasInput={dirty} />
    </div>
  </Modal>;
}
