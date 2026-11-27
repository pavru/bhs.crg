import { useState } from 'react';
import { useCan } from '@/shared/api/access';
import type { InvoiceView } from '@/shared/api/invoices';
import { purchaseRefusal, REQUEST_READ, usePurchaseRequest, useSubmittedPurchases } from '@/shared/api/purchaseRequests';
import { formatDate } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { choicesForInvoice, constructionForRequest } from './invoiceRequestChoices';

const NONE = 'none';

interface Props {
  invoice: InvoiceView; selection: string | null | undefined; editable: boolean; autosave: boolean;
  onSelect: (id: string | null) => void;
}

export function InvoicePurchaseRequestField(props: Props) {
  return props.invoice.purchaseRequestId === undefined ? null : <PurchaseLink {...props} />;
}

function PurchaseLink({ invoice, selection, editable, autosave, onSelect }: Props) {
  const can = useCan();
  const allowed = can.permission(REQUEST_READ);
  const site = constructionForRequest(invoice);
  const saved = invoice.purchaseRequestId ?? null;
  const value = selection === undefined ? saved : selection;
  const [draft, setDraft] = useState<{ value: string | null; base: string | null }>();
  const selected = draft?.base === value ? draft.value : value;
  const list = useSubmittedPurchases(site, editable && allowed);
  const choices = choicesForInvoice(list.data ?? [], site);
  const listed = choices.find(row => row.id === saved);
  const current = usePurchaseRequest(saved ?? undefined, !!saved && allowed && !listed);
  const known = allowed && (listed ?? current.data);
  const caption = saved && known ? `Заявка № ${known.number}` : saved ? 'Заявка связана' : 'Без заявки';
  const unavailable = !allowed ? 'Для выбора нужно право чтения заявок.'
    : !site ? 'Для привязки полностью разнесите счёт на одну стройку.'
    : list.isError ? purchaseRefusal(list.error).message
    : list.isPending ? 'Читаем поданные заявки…'
    : !choices.length ? 'На стройке нет поданных заявок.' : undefined;
  const mayChoose = editable && allowed && !!site && !list.isError && !list.isPending;
  return <div className="grid gap-2 border-t border-outline-variant pt-3" data-testid="invoice-purchase-request">
    {mayChoose ? <div className="@container/request-link">
      <div className="grid items-start gap-2 @lg/request-link:grid-cols-[minmax(0,1fr)_auto]">
        <Select label="Заявка на закупку" value={selected ?? NONE}
          onValueChange={id => setDraft({ value: id === NONE ? null : id, base: value })}>
          <SelectItem value={NONE}>Без заявки</SelectItem>
          {choices.map(row => <SelectItem key={row.id} value={row.id}>№ {row.number} · {formatDate(row.createdAt)}</SelectItem>)}
          {value && !choices.some(row => row.id === value) && <SelectItem value={value} disabled>{caption}</SelectItem>}
        </Select>
        <Button variant="filled" disabled={selected === value || (!!selected && !choices.some(row => row.id === selected))}
          className="@lg/request-link:self-center" onClick={() => { setDraft(undefined); onSelect(selected); }}>
          {autosave ? 'Сохранить связь' : 'Добавить к правкам'}
        </Button>
      </div>
    </div> : <div className="grid gap-1">
      <p className="text-body-small text-on-surface-variant">Заявка на закупку</p>
      <p className="text-body-medium">{value === null ? 'Без заявки' : caption}</p>
      {editable && !!value && <Button variant="text" className="justify-self-start" onClick={() => onSelect(null)}>Снять связь</Button>}
    </div>}
    {editable && !autosave && selection !== undefined &&
      <p className="text-body-small text-on-surface-variant">Связь запишется вместе с остальными правками счёта.</p>}
    {editable && unavailable && <p className="text-body-small text-on-surface-variant">{unavailable}</p>}
  </div>;
}
