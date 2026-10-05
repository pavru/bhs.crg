import { useRef, useState } from 'react';
import { Banknote, Lock, Undo2 } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { DateField } from '@/shared/ui/DateField';
import { TextField } from '@/shared/ui/TextField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useToast } from '@/shared/ui/Toast';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { useCan } from '@/shared/api/access';
import type { InvoiceView } from '@/shared/api/invoices';
import {
  useCancelPayment, useDescribePayment, useFirstPaymentPreview, usePayInvoice, usePaymentPreview,
  usePostedPayment,
  type PaymentPosting,
} from '@/shared/api/invoicePayment';
import { K, asInput } from './invoiceFields';
import { formatDate, formatMoney } from '@/shared/format/format';
import { PaymentPostingTable } from './PaymentPostingTable';
import { changedRows, paidToast, payLabel } from './paymentPosting';

/**
 * Оплата счёта в форме (задача C5, issue #1082): отметка, расклад по периодам, отмена — и полоса
 * «заперт», когда оплаченный счёт попал в закрытый период.
 *
 * <p><b>Клиент не считает ничего.</b> Почему оплатить нельзя, какая доля куда переносится и чем счёт
 * заперт — слова сервера. Признак запертого — только `payment.lockedBy`, а не «оплачен»: оплаченный
 * счёт открытого периода правится.</p>
 */

/** Как счёт назван в заголовках диалогов. */
function invoiceTitle(view: InvoiceView): string {
  const number = asInput(view.requisites[K.number]);
  const date = asInput(view.requisites[K.date]);
  return `счёт ${number ? `№ ${number}` : 'без номера'}${date ? ` от ${formatDate(date)}` : ''}`;
}

/** Полоса запертого счёта: причина словами сервера и что именно нельзя. */
export function InvoiceLockNote({ lockedBy }: { lockedBy: string }) {
  const can = useCan();

  return (
    <div role="note" className="flex items-start gap-2 rounded-lg border border-stroke bg-surface2 px-3 py-2
      text-xs text-fg2">
      <Lock size={14} className="shrink-0 mt-0.5 text-fg3" />
      <span>
        Счёт заперт: {lockedBy}. Изменить нельзя поля, строки, разноску и отметку оплаты.
        {/* Действие называем только тому, у кого есть путь: остальным ссылка вела бы в отказ. */}
        {can.permission('core.period.close') && (
          <> Закрытие отменяют на странице <Link to="/periods" className="underline">«Учётный период»</Link>.</>
        )}
      </span>
    </div>
  );
}

export function InvoicePayment({ view, dirty }: {
  view: InvoiceView;
  /** В форме есть несохранённые правки: оплата считается по сохранённому счёту. */
  dirty: boolean;
}) {
  const can = useCan();
  const canPay = can.permission('costs.invoice.pay');
  const [dialog, setDialog] = useState<null | 'pay' | 'cancel' | 'posting'>(null);
  const payment = view.payment;
  const locked = payment.lockedBy !== null;

  if (!payment.paid) {
    if (!canPay) return null;

    return (
      <div className="flex items-center gap-2 flex-wrap text-xs">
        <Button size="sm" variant="tonal" icon={<Banknote size={14} />} disabled={dirty}
          onClick={() => setDialog('pay')}>
          Отметить оплату
        </Button>
        {/* Причина — рядом и видимая: приглушённая кнопка сама о ней молчит. */}
        {dirty && <span className="text-fg3">сохраните правки счёта: оплата считается по сохранённому</span>}
        {!dirty && payment.refusal && <span className="text-warning">оплатить нельзя: {payment.refusal}</span>}
        {dialog === 'pay' && <PayDialog view={view} onDone={() => setDialog(null)} />}
      </div>
    );
  }

  return (
    <div className="flex items-center gap-x-4 gap-y-1 flex-wrap text-xs text-fg2">
      <PaidDocument view={view} editable={canPay && !locked} />
      <span>
        Учётный период: <b className="tabular-nums">{payment.periods.join(' + ') || '—'}</b>{' '}
        <button type="button" className="underline decoration-dotted underline-offset-2 text-fg3"
          onClick={() => setDialog('posting')}>
          расклад
        </button>
      </span>
      {canPay && (locked
        ? <span className="text-fg3">Отмена оплаты недоступна: период закрыт</span>
        : (
          <Button size="sm" variant="text" danger icon={<Undo2 size={13} />} onClick={() => setDialog('cancel')}>
            Отменить оплату
          </Button>
        ))}
      {dialog === 'cancel' && <CancelDialog view={view} onDone={() => setDialog(null)} />}
      {dialog === 'posting' && <PostedDialog view={view} onDone={() => setDialog(null)} />}
    </div>
  );
}

/** «Оплачен … · платёжный документ: …» с правкой документа на месте — своей кнопкой, не общим сохранением. */
function PaidDocument({ view, editable }: { view: InvoiceView; editable: boolean }) {
  const describe = useDescribePayment();
  const toast = useToast();
  const [draft, setDraft] = useState<string | null>(null);
  const payment = view.payment;

  if (draft !== null)
    return (
      <span className="inline-flex items-end gap-2">
        <TextField label="Платёжный документ" value={draft} onChange={e => setDraft(e.target.value)} />
        <Button size="sm" variant="filled" loading={describe.isPending}
          onClick={async () => {
            try {
              await describe.mutateAsync({ id: view.id, version: view.version, document: draft.trim() || null });
              setDraft(null);
            } catch (e) {
              toast.apiError(e, 'Платёжный документ не сохранён');
            }
          }}>
          Сохранить
        </Button>
        <Button size="sm" variant="text" onClick={() => setDraft(null)}>Отмена</Button>
      </span>
    );

  return (
    <span title="Дату меняют отменой оплаты и новой отметкой">
      Оплачен <b className="tabular-nums">{payment.paidOn ? formatDate(payment.paidOn) : '—'}</b>
      {' · '}платёжный документ: {payment.document ?? 'не указан'}{' '}
      {editable && (
        <button type="button" className="underline decoration-dotted underline-offset-2 text-fg3"
          onClick={() => setDraft(payment.document ?? '')}>
          изменить
        </button>
      )}
    </span>
  );
}

/**
 * Диалог отметки оплаты.
 *
 * <p>⚠️ Записать можно только по СВЕЖЕМУ раскладу: пока идёт пересчёт после смены даты, кнопка
 * неактивна — иначе человек подтвердил бы расклад прежней даты. Сервер это ловит отметкой расклада,
 * но отказ после нажатия хуже кнопки, которая честно ждёт.</p>
 */
function PayDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const toast = useToast();
  const preview = usePaymentPreview();
  const pay = usePayInvoice();
  // Первый вопрос — без даты: «сегодня» называет сервер. Пока ответ не пришёл, поле даты пустое — а
  // не сегодняшнее число браузера: его часы могут стоять в другом поясе, чем часы компании.
  const first = useFirstPaymentPreview(view.id);
  const [asked, setAsked] = useState<PaymentPosting | null>(null);
  const [date, setDate] = useState<string | null>(null);
  const [document, setDocument] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const [changed, setChanged] = useState<ReadonlySet<string>>(new Set());
  const turns = useRef(0);

  const posting = asked ?? first.data ?? null;
  const paidOn = date ?? posting?.paidOn ?? '';
  // Ошибка первого вопроса жива, только пока на него не ответили позже: запрос больше не повторяется,
  // и без этого условия удачный повтор оставлял бы диалог в «расклад не получен» до закрытия.
  const failed = refused ?? (asked === null && first.error ? apiError(first.error, 'сервер не ответил') : null);

  async function ask(date: string | null, after?: PaymentPosting) {
    const turn = ++turns.current;
    setRefused(null);
    try {
      const next = await preview.mutateAsync({ id: view.id, paidOn: date });
      // Ответ на прежний вопрос, пришедший после нового, выбрасываем: иначе расклад одной даты
      // оказался бы под другой.
      if (turn !== turns.current) return;
      setChanged(after ? changedRows(after.rows, next.rows) : new Set());
      setAsked(next);
      setDate(next.paidOn);
    } catch (e) {
      if (turn === turns.current) setRefused(apiError(e, 'сервер не ответил'));
    }
  }

  const fresh = posting !== null && !preview.isPending && posting.paidOn === paidOn && failed === null;
  const payable = fresh && !posting.refusal && !posting.dateRefusal;

  async function submit() {
    if (!posting || !payable) return;
    setError(null);
    try {
      await pay.mutateAsync({
        id: view.id, version: view.version, paidOn, document: document.trim() || null, seen: posting.stamp,
      });
      toast.success(paidToast(paidOn, posting.rows));
      onDone();
    } catch (e) {
      // Причин у 409 три — закрыли период, поправили разноску, сменили сумму, — и какая из них,
      // знает сервер: показываем его текст и свежий расклад с подсветкой изменившегося.
      setError(apiError(e, 'Оплата не записана.'));
      if (isConflict(e)) await ask(paidOn, posting);
    }
  }

  // Отказ, не зависящий от даты: заполнять нечего — только причина.
  if (posting?.refusal)
    return (
      <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Оплата: ${invoiceTitle(view)}`}
        footer={<div className="flex justify-end"><Button variant="filled" onClick={onDone}>Понятно</Button></div>}>
        <p role="alert" className="text-[13px] text-fg1">
          Оплатить нельзя: {posting.refusal}.
          {posting.refusal.includes('расходится') && ' Исправьте строки или сумму счёта.'}
        </p>
      </Modal>
    );

  return (
    <Modal open wide onOpenChange={o => { if (!o) onDone(); }} title={`Оплата: ${invoiceTitle(view)}`}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onDone}>Отмена</Button>
          <Button variant="filled" disabled={!payable} loading={pay.isPending} onClick={submit}>
            {posting && payable ? payLabel(posting.rows) : 'Отметить оплату'}
          </Button>
        </div>
      }>
      <div className="space-y-4">
        {error && (
          <p role="alert" className="text-[13px] text-danger">
            {error}{changed.size > 0 && ' Расклад обновлён — проверьте перенос.'}
          </p>
        )}

        <div className="grid grid-cols-2 gap-3">
          <DateField label="Дата платежа" value={paidOn} required
            hint={posting?.dateRefusal ?? undefined}
            onChange={iso => {
              const next = iso ? iso.slice(0, 10) : '';
              setDate(next);
              if (next) void ask(next);
            }} />
          <TextField label="Платёжный документ" value={document} hint="необязательно; можно дописать позже"
            onChange={e => setDocument(e.target.value)} />
        </div>

        {posting?.total != null && (
          <p className="text-[13px] text-fg2">К оплате <b className="tabular-nums">{formatMoney(posting.total)}</b></p>
        )}

        <div>
          <h3 className="text-xs font-medium text-fg3 mb-1.5">Когда войдёт в затраты</h3>
          {failed
            ? (
              <p role="alert" className="text-[13px] text-danger">
                Расклад не получен: {failed}{' '}
                <button type="button" className="underline" onClick={() => void ask(paidOn || null)}>Повторить</button>
              </p>
            )
            : posting === null ? <p className="text-[13px] text-fg3">Считаем расклад…</p>
              : posting.dateRefusal ? <p className="text-[13px] text-warning">Оплатить этой датой нельзя: {posting.dateRefusal}.</p>
                : (
                  <div className={fresh ? '' : 'opacity-50'} aria-busy={!fresh}>
                    {!fresh && <p className="text-[13px] text-fg3 mb-1">пересчитываем…</p>}
                    <PaymentPostingTable key={posting.stamp} posting={posting} changed={changed} />
                  </div>
                )}
        </div>
      </div>
    </Modal>
  );
}

/** Отмена ошибочной отметки — с причиной: версий оплата не создаёт, и другого следа не остаётся. */
function CancelDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const toast = useToast();
  const cancel = useCancelPayment();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Отменить оплату: ${invoiceTitle(view)}`}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onDone}>Не отменять</Button>
          <Button variant="filled" danger disabled={!reason.trim()} loading={cancel.isPending}
            onClick={async () => {
              setError(null);
              try {
                await cancel.mutateAsync({ id: view.id, version: view.version, reason });
                toast.success('Оплата отменена: счёт снова не оплачен.');
                onDone();
              } catch (e) {
                setError(apiError(e, 'Оплата не отменена.'));
              }
            }}>
            Отменить оплату
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <p className="text-[13px] text-fg2">
          Счёт вернётся в «не оплачен», учётные даты долей сотрутся. Запись об оплате в журнале действий
          остаётся: отмена записывается рядом, с причиной.
        </p>
        <TextAreaField label="Причина отмены" value={reason} onChange={e => setReason(e.target.value)}
          rows={3} required hint="её увидят в журнале действий" />
        {error && <p role="alert" className="text-[13px] text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

/** Записанный расклад — та же таблица, только чтение. */
function PostedDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const { data, error, isLoading } = usePostedPayment(view.id, true);

  return (
    <Modal open wide onOpenChange={o => { if (!o) onDone(); }} title={`Расклад оплаты: ${invoiceTitle(view)}`}
      footer={<div className="flex justify-end"><Button variant="text" onClick={onDone}>Закрыть</Button></div>}>
      {isLoading ? <p className="text-[13px] text-fg3">Читаем расклад…</p>
        : error || !data ? <p role="alert" className="text-[13px] text-danger">Расклад не получен: {apiError(error, 'сервер не ответил')}</p>
          : <PaymentPostingTable posting={data} expanded />}
    </Modal>
  );
}
