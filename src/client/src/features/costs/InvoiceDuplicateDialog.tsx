import { useState } from 'react';
import type { InvoiceDuplicateBlocker, InvoiceView } from '@/shared/api/invoices';
import { useConfirmInvoiceDuplicate } from '@/shared/api/invoicePayment';
import { formatDate, formatDateTime, formatMoney } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { InvoiceFileButton } from './InvoiceFileButton';
import { K, asInput, duplicateLabel } from './invoiceFields';
import { toNumber } from './invoiceLines';

type ComparisonInvoice = Pick<InvoiceDuplicateBlocker,
  'id' | 'number' | 'issuedOn' | 'total' | 'paid' | 'paidOn' | 'createdAt' | 'scanBlobPath' | 'scanFileName'>;

function currentCard(view: InvoiceView): ComparisonInvoice {
  const file = view.requisites[K.scan] as { fileName?: string; blobPath?: string } | null;
  return {
    id: view.id, number: asInput(view.requisites[K.number]), issuedOn: asInput(view.requisites[K.date]).slice(0, 10),
    total: toNumber(asInput(view.requisites[K.total])), createdAt: view.createdAt,
    paid: view.payment.paid, paidOn: view.payment.paidOn,
    scanBlobPath: file?.blobPath ?? null, scanFileName: file?.fileName ?? null,
  };
}

export function InvoiceDuplicateDialog({ view, initialId, supplierName, canPay, onClose, onDone, onOpenInvoice }: {
  view: InvoiceView;
  initialId: string;
  supplierName: string | null;
  canPay: boolean;
  onClose: () => void;
  onDone: () => void;
  onOpenInvoice: (id: string) => void;
}) {
  const [shown] = useState(() => ({
    current: currentCard(view), version: view.version, blockers: view.payment.duplicateBlockers ?? [], supplierName,
  }));
  const [selected, setSelected] = useState(initialId);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [conflicted, setConflicted] = useState(false);
  const confirm = useConfirmInvoiceDuplicate();
  const toast = useSnackbar();
  const other = shown.blockers.find(blocker => blocker.id === selected);
  if (!other) return (
    <Modal open title="Сверка счетов" acknowledge="Понятно" onOpenChange={open => { if (!open) onClose(); }}>
      <p>Пара изменилась. Откройте сверку заново, чтобы увидеть свежие данные.</p>
    </Modal>
  );

  const stale = conflicted || shown.version !== view.version || !view.payment.duplicateBlockers?.some(
    blocker => blocker.id === selected && blocker.otherRevision === other.otherRevision,
  );
  const message = reason.trim();

  async function submit() {
    if (!canPay || !other || stale || !message) return;
    setError(null);
    try {
      await confirm.mutateAsync({
        id: view.id, version: shown.version, otherInvoiceId: other.id, otherRevision: other.otherRevision, reason: message,
      });
      toast.success('Подтверждено, что счета разные.');
      onDone();
    } catch (e) {
      if (isConflict(e)) setConflicted(true);
      setError(apiError(e, 'Подтверждение не записано.'));
    }
  }

  return (
    <Modal open wide title="Сверка счетов" busy={confirm.isPending} isDirty={reason !== ''}
      onOpenChange={open => { if (!open) onClose(); }}
      footer={canPay ? <>
        <ModalExit>Отмена</ModalExit>
        <ModalPrimary disabled={stale || !message} loading={confirm.isPending} onClick={submit}>Это не дубль</ModalPrimary>
      </> : undefined}>
      <div className="grid gap-4">
        {shown.blockers.length > 1 && <Select label="Счёт для сверки" value={selected}
          onValueChange={id => { setSelected(id); setReason(''); setError(null); }}>
          {shown.blockers.map(blocker => <SelectItem key={blocker.id} value={blocker.id}>
            № {duplicateLabel(blocker)} · {formatDateTime(blocker.createdAt)}
          </SelectItem>)}
        </Select>}
        <div className="grid gap-3 md:grid-cols-2">
          <ComparisonCard title="Открытый счёт" card={shown.current} supplierName={shown.supplierName} />
          <ComparisonCard title="Двойник" card={other} supplierName={shown.supplierName}
            onOpen={() => onOpenInvoice(other.id)} />
        </div>
        {(error || stale) && <p role="alert" className="text-body-medium text-error">
          {error ?? 'Пара изменилась. Откройте сверку заново, чтобы увидеть свежие данные.'}
        </p>}
        {canPay && <TextAreaField label="Причина" required maxLength={500} value={reason}
          hint="Коротко укажите, почему это разные счета." onChange={e => setReason(e.target.value)} />}
      </div>
    </Modal>
  );
}

function ComparisonCard({ title, card, supplierName, onOpen }: {
  title: string; card: ComparisonInvoice; supplierName: string | null; onOpen?: () => void;
}) {
  const facts = [
    ['Поставщик', supplierName ?? 'Название недоступно'],
    ['Дата счёта', card.issuedOn ? formatDate(card.issuedOn) : 'Не указана'],
    ['К оплате', card.total === null ? 'Не указана' : formatMoney(card.total)],
    ['Заведён', formatDateTime(card.createdAt)],
  ];
  return (
    <section aria-label={title} className="min-w-0">
      <Card className="h-full">
        <div>
          <p className="text-label-medium text-on-surface-variant">{title}</p>
          <h3 className="text-title-medium text-on-surface">Счёт № {card.number}</h3>
          <StatusText tone={card.paid ? 'success' : 'neutral'}>
            {card.paid ? `Оплачен${card.paidOn ? ` ${formatDate(card.paidOn)}` : ''}` : 'Не оплачен'}
          </StatusText>
        </div>
        <dl className="grid grid-cols-2 gap-3 break-words">
          {facts.map(([label, value]) => <div key={label}>
            <dt className="text-label-medium text-on-surface-variant">{label}</dt>
            <dd className="text-body-medium text-on-surface">{value}</dd>
          </div>)}
        </dl>
        {card.scanBlobPath
          ? <div><InvoiceFileButton invoiceId={card.id} file="over" blobPath={card.scanBlobPath}
              fileName={card.scanFileName} showName onToggle={() => {}} /></div>
          : <p>Файл не приложен</p>}
        {onOpen && <Button variant="text" className="self-start" onClick={onOpen}>Открыть счёт</Button>}
      </Card>
    </section>
  );
}
