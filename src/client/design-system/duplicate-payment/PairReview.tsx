import { useState, type ReactNode } from 'react';
import { Button } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Description } from '@/shared/ui/icons';
import { ListItem, LIST_CONTAINER } from '@/shared/ui/ListItem';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { date, number, supplier, type Invoice, type InvoiceId } from './fixtures';

export type Variant = 'pair' | 'sheet' | 'compare';

export function PaymentStatus({ paid }: { paid: boolean }) {
  return <StatusText tone={paid ? 'success' : 'neutral'}>{paid ? 'Оплачен' : 'Не оплачен'}</StatusText>;
}

export function InvoiceFacts({ invoice }: { invoice: Invoice }) {
  return (
    <dl className="grid grid-cols-2 gap-4 break-words">
      <div><dt>Поставщик</dt><dd>{supplier}</dd></div>
      <div><dt>Дата счёта</dt><dd>{date}</dd></div>
      <div><dt>Стройка</dt><dd>{invoice.site}</dd></div>
      <div><dt>К оплате</dt><dd>{invoice.amount}</dd></div>
    </dl>
  );
}

export function FileLink({ invoice }: { invoice: Invoice }) {
  const url = new URL(location.href);
  url.searchParams.set('file', invoice.id);
  return (
    <a href={url.href} target="_blank" rel="noreferrer" aria-label={`Открыть файл счёта ${invoice.id} в новой вкладке`}
      className="inline-flex min-h-11 items-center gap-2 break-all text-primary underline underline-offset-4">
      <Icon icon={Description} size={18} className="shrink-0" />{invoice.file}
    </a>
  );
}

export function DuplicateRow({ invoice, current, paid, selected, onSelect, action }: {
  invoice: Invoice;
  current: Invoice;
  paid: boolean;
  selected?: boolean;
  onSelect: () => void;
  action?: ReactNode;
}) {
  return (
    <ListItem onClick={onSelect} selected={selected} action={action}
      headline={`Счёт ${invoice.id} · № ${number}`}
      supporting={<span className="flex flex-col whitespace-normal">
        <span>{invoice.site} · {invoice.amount}</span>
        <PaymentStatus paid={paid} />
        {invoice.file === current.file && <span>Тот же файл</span>}
      </span>} />
  );
}

type ReviewProps = {
  current: Invoice;
  blockers: Invoice[];
  initialOther: InvoiceId;
  notice: string;
  canPay: boolean;
  isPaid: (id: InvoiceId) => boolean;
  onOpenInvoice: (id: InvoiceId) => void;
  onConfirm: (id: InvoiceId, reason: string) => void;
  onClose: () => void;
};

function ReasonField({ reason, onChange }: { reason: string; onChange: (value: string) => void }) {
  return <TextAreaField label="Причина" hint="Коротко укажите, почему это разные счета."
    required value={reason} onChange={event => onChange(event.target.value)} />;
}

export function PairReview({ variant, ...props }: ReviewProps & { variant: Variant }) {
  const { current, blockers, canPay, isPaid, onOpenInvoice, onConfirm, onClose, notice } = props;
  const [selected, setSelected] = useState(props.initialOther);
  const [reason, setReason] = useState('');
  const other = blockers.find(item => item.id === selected) ?? blockers[0];
  const select = (id: InvoiceId) => {
    if (id === selected) return;
    setSelected(id);
    setReason('');
  };
  const confirm = () => {
    const message = reason.trim();
    if (!canPay || !message) return;
    const next = blockers.find(invoice => invoice.id !== other.id);
    if (next) setSelected(next.id);
    setReason('');
    onConfirm(other.id, message);
  };
  const field = canPay && <ReasonField reason={reason} onChange={setReason} />;

  if (variant === 'sheet') {
    return (
      <SideSheet open variant="modal" title="Проверить совпадения" onClose={onClose}>
        <div className="flex flex-col gap-4">
          <h3 className="text-title-small">Мешает оплате</h3>
          <p className="text-body-medium text-on-surface-variant">Совпали поставщик, номер и дата счёта.</p>
          <ul className={LIST_CONTAINER} aria-label="Блокирующие счета">
            {blockers.map(invoice => <li key={invoice.id}><DuplicateRow invoice={invoice} current={current}
              paid={isPaid(invoice.id)} onSelect={() => select(invoice.id)} selected={invoice.id === other.id} /></li>)}
          </ul>
          <section aria-label="Выбранная пара" className="flex flex-col items-start gap-3 border-t border-outline-variant pt-4">
            <h3 className="text-title-small">Счёт {current.id} и счёт {other.id}</h3>
            <Button variant="text" onClick={() => onOpenInvoice(other.id)}>Открыть счёт {other.id}</Button>
            {canPay && <div className="grid w-full gap-3">
              {field}
              <Button className="justify-self-end" variant="filled" disabled={!reason.trim()} onClick={confirm}>Это не дубль</Button>
            </div>}
          </section>
          <p role="status" className="text-body-medium text-on-surface-variant">{notice}</p>
        </div>
      </SideSheet>
    );
  }

  const compare = variant === 'compare';
  return (
    <Modal open title={compare ? 'Сверка счетов' : 'Это не дубль?'} wide={compare}
      onOpenChange={open => { if (!open) onClose(); }}
      footer={canPay ? <><ModalExit>Отмена</ModalExit><ModalPrimary disabled={!reason.trim()} onClick={confirm}>Это не дубль</ModalPrimary></> : undefined}>
      <div className="grid gap-4">
        {compare ? <>
          {blockers.length > 1 && <Select label="Счёт для сверки" value={other.id}
            onValueChange={id => { const invoice = blockers.find(item => item.id === id); if (invoice) select(invoice.id); }}>
            {blockers.map(invoice => <SelectItem key={invoice.id} value={invoice.id}>Счёт {invoice.id} · {invoice.site} · {invoice.amount}</SelectItem>)}
          </Select>}
          <div className="grid gap-3 md:grid-cols-2">
            <ComparisonCard invoice={current} paid={isPaid(current.id)} current />
            <ComparisonCard invoice={other} paid={isPaid(other.id)} onOpen={() => onOpenInvoice(other.id)} />
          </div>
        </> : <>
          <p>У счетов {current.id} и {other.id} совпали поставщик, номер и дата.</p>
          <div className="rounded-md bg-surface-container px-4 py-3">
            <ListItem passive headline={`Счёт ${other.id} · № ${number}`}
              supporting={<span className="flex flex-col whitespace-normal"><span>{other.site} · {other.amount}</span><PaymentStatus paid={isPaid(other.id)} /></span>} />
          </div>
        </>}
        {field}
      </div>
    </Modal>
  );
}

function ComparisonCard({ invoice, paid, current, onOpen }: { invoice: Invoice; paid: boolean; current?: boolean; onOpen?: () => void }) {
  return (
    <section className="grid min-w-0 content-start gap-3 rounded-lg border border-outline-variant bg-surface p-4">
      <div>
        <p className="text-label-medium text-on-surface-variant">{current ? 'Открытый счёт' : 'Двойник'}</p>
        <h3 className="text-title-medium">Счёт {invoice.id} · № {number}</h3>
        <PaymentStatus paid={paid} />
      </div>
      <div className="invoice-facts"><InvoiceFacts invoice={invoice} /></div>
      <FileLink invoice={invoice} />
      {onOpen && <Button variant="text" className="justify-self-start" onClick={onOpen}>Открыть счёт {invoice.id}</Button>}
    </section>
  );
}
