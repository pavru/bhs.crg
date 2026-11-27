import { useState } from 'react';
import {
  purchaseRefusal, useWritePurchase, type PurchaseQuantity, type PurchaseRequest, type PurchaseStatus,
  type PurchaseWrite,
} from '@/shared/api/purchaseRequests';
import { formatDate, formatNumber } from '@/shared/format/format';
import { useAuth } from '@/shared/hooks/useAuth';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { PurchaseLineFields } from './PurchaseLineFields';
import { confirmedLines, lineDrafts, materialName } from './purchaseLineDraft';

const PURCHASE_STATUS = { draft: 'Черновик', submitted: 'Подана', closed: 'Закрыта' } as const;

export function PurchaseStatusText({ status }: { status: PurchaseStatus }) {
  return <StatusText tone={status === 'submitted' ? 'primary' : 'neutral'}>{PURCHASE_STATUS[status]}</StatusText>;
}

type Action = PurchaseWrite['action'];
const LABEL: Record<Action, string> = {
  lines: 'Изменить строки', submit: 'Подать заявку', close: 'Закрыть заявку',
  return: 'Вернуть в черновик', delete: 'Удалить черновик',
};

function show(value: number | null) { return value === null ? 'Неизвестно' : formatNumber(value, 6); }

function PurchaseTotals({ request, invoices, issued }: { request: PurchaseRequest; invoices: boolean; issued: boolean }) {
  const quantities = request.quantities;
  if (!quantities) return null;
  const invoiceVisible = invoices && quantities.invoiced.state === 'shown';
  const issuedVisible = issued && quantities.issuedForConstruction.state === 'shown';
  const columns: DataGridColumn<PurchaseQuantity>[] = [
    { key: 'name', label: 'Материал', wrap: true, rowHeader: true, cell: row => <>
      {materialName(request.nomenclatureNames, row.nomenclatureId)}
      <span className="block text-body-small text-on-surface-variant">{row.unit ?? 'Без единицы'}</span>
    </> },
    { key: 'ordered', label: 'Заказано', align: 'right', cell: row => show(row.orderedQuantity) },
    { key: 'invoiced', label: 'Выставлено', align: 'right', wrap: true, cell: row => invoiceVisible ? <>
      {show(row.invoicedQuantity)}
      {row.invoicedUnitMismatch && <StatusText tone="warning" className="mt-1">Другая единица</StatusText>}
    </> : 'Скрыто' },
    { key: 'issued', label: 'Отпущено по всей стройке', align: 'right', wrap: true, cell: row => issuedVisible ? <>
      {show(row.issuedForConstructionQuantity)}
      {row.issuedForConstructionUnitMismatch && <StatusText tone="warning" className="mt-1">Другая единица</StatusText>}
    </> : 'Скрыто' },
  ];
  return <div className="@container/quantities grid min-w-0 gap-3">
    {!invoiceVisible && <Banner tone="info">«Выставлено» скрыто. Нужно право чтения счетов.</Banner>}
    {!issuedVisible && <Banner tone="info">«Отпущено» скрыто. Нужно право чтения накладных или количеств отпуска.</Banner>}
    <div className="hidden min-w-0 @xl/quantities:block">
      <DataGrid columns={columns} rows={quantities.items} rowKey={(row, index) => `${row.nomenclatureId}:${index}`} />
    </div>
    <div className="grid gap-2 @xl/quantities:hidden">
      {quantities.items.map((row, index) => <Card key={`${row.nomenclatureId}:${index}`} title={columns[0].cell?.(row, index)}>
        <dl className="grid gap-1 text-body-medium">
          {columns.slice(1).map(column => <div key={column.key} className="flex justify-between gap-2">
            <dt className="text-on-surface-variant">{column.label}</dt>
            <dd className="text-right tabular-nums">{column.cell?.(row, index)}</dd>
          </div>)}
        </dl>
      </Card>)}
    </div>
  </div>;
}

function PurchaseActionDialog({ action, request, onClose, onDone, onReload }: {
  action: Action; request: PurchaseRequest; onClose: () => void; onDone: () => void; onReload: () => void;
}) {
  const [version] = useState(request.version);
  const [draft, setDraft] = useState(() => lineDrafts(request.lines));
  const [reason, setReason] = useState('');
  const write = useWritePurchase();
  const lines = confirmedLines(draft);
  const failure = write.error ? purchaseRefusal(write.error) : undefined;
  const resource = { id: request.id, version };
  async function save() {
    const payload = action === 'lines' ? lines && { ...resource, action, lines }
      : action === 'return' ? { ...resource, action, reason: reason.trim() } : { ...resource, action };
    if (!payload) return;
    try { await write.mutateAsync(payload); onDone(); }
    catch { /* Показывается в этом окне. */ }
  }
  return <Modal open title={LABEL[action]} wide={action === 'lines'}
    question={action === 'close' || action === 'delete' || action === 'submit'}
    busy={write.isPending} onOpenChange={open => !open && onClose()}
    footer={<ModalButtons><ModalExit>Отмена</ModalExit>
      <ModalPrimary onClick={() => void save()} disabled={failure?.conflict || (action === 'lines' && !lines)
        || (action === 'return' && !reason.trim())}>
        {action === 'lines' ? 'Сохранить строки' : LABEL[action]}
      </ModalPrimary>
    </ModalButtons>}>
    <div className="grid gap-3">
      {failure && <Banner tone="error" actions={failure.conflict ? [{ label: 'Перечитать', onClick: onReload }] : []}>
        {failure.message}
      </Banner>}
      {action === 'lines' ? <PurchaseLineFields draft={draft} names={request.nomenclatureNames} busy={write.isPending} onChange={setDraft} />
        : action === 'return' ? <TextField required maxLength={2000} label="Причина возврата" value={reason} disabled={write.isPending}
          onChange={event => setReason(event.target.value)} />
        : <p className="text-body-medium">{action === 'delete' ? `Удалить заявку № ${request.number}?`
          : action === 'submit' ? 'После подачи правка строк будет закрыта.'
          : 'Связанные счета сохранятся. Новые счета к закрытой заявке привязать нельзя.'}</p>}
    </div>
  </Modal>;
}

export function PurchaseDetail({ request, site, editable, invoices, issued, onDeleted, onReload }: {
  request: PurchaseRequest; site: string; editable: boolean; invoices: boolean; issued: boolean;
  onDeleted: () => void; onReload: () => void;
}) {
  const [action, setAction] = useState<Action>();
  const me = useAuth().user?.sub;
  return <article className="grid w-full min-w-0 content-start gap-4 overflow-y-auto p-4 @2xl:p-5" data-testid="purchase-detail">
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-title-large">Заявка № {request.number}</h2><PurchaseStatusText status={request.status} />
      </div>
      <p className="text-body-medium text-on-surface-variant">{site}</p>
      <p className="text-body-small text-on-surface-variant">Создана {formatDate(request.createdAt)}</p>
    </div>
    <PurchaseTotals request={request} invoices={invoices} issued={issued} />
    {request.lines.some(row => row.note) && <dl className="grid gap-2 text-body-medium">
      {request.lines.filter(row => row.note).map((row, index) => <div key={index}>
        <dt className="text-on-surface-variant">{materialName(request.nomenclatureNames, row.nomenclatureId)}</dt>
        <dd className="whitespace-pre-wrap">{row.note}</dd>
      </div>)}
    </dl>}
    {editable && request.status === 'draft' && <div className="flex flex-wrap gap-2">
      <Button variant="filled" onClick={() => setAction('submit')}>{LABEL.submit}</Button>
      <Button variant="outlined" onClick={() => setAction('lines')}>{LABEL.lines}</Button>
      <Button variant="text" onClick={() => setAction('delete')}>{LABEL.delete}</Button>
    </div>}
    {editable && request.status === 'submitted' && <div className="flex flex-wrap gap-2">
      <Button variant="outlined" onClick={() => setAction('close')}>{LABEL.close}</Button>
      {request.createdBy === me && <Button variant="text" onClick={() => setAction('return')}>{LABEL.return}</Button>}
    </div>}
    {editable && action && <PurchaseActionDialog request={request} action={action} onClose={() => setAction(undefined)}
      onReload={() => { setAction(undefined); onReload(); }} onDone={() => {
        setAction(undefined);
        if (action === 'delete') onDeleted();
      }} />}
  </article>;
}
