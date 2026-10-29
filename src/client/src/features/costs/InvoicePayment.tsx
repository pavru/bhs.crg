import { useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { LockIcon, Payments, Undo } from '@/shared/ui/icons';
import { Link } from 'react-router';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
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
import { K, REJECTED, asInput } from './invoiceFields';
import { formatDate, formatMoney } from '@/shared/format/format';
import { PaymentPostingTable } from './PaymentPostingTable';
import { changedRows, paidToast, payLabel } from './paymentPosting';
import { Tooltip } from '@/shared/ui/Tooltip';

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
    <div role="note" className="flex items-start gap-2 rounded-md border border-outline-variant bg-surface-container px-3 py-2
      text-body-small text-on-surface-variant">
      <Icon icon={LockIcon} size={18} className="shrink-0 mt-0.5 text-on-surface-variant" />
      <span>
        Счёт заперт: {lockedBy}. Поля, строки и разноску обычной правкой не изменить.
        {/* Путь исправления (E2, PR 6b-1) называем тому, у кого есть кнопка «Исправить», — остальным он закрыт. */}
        {can.permission('costs.invoice.edit') && can.permission('costs.invoice.correct') && (
          <> Изменить их можно исправлением — кнопкой «Исправить»: оно ляжет в книгу, закрытый месяц не изменится.</>
        )}
        {/* Отмена оплаты такому счёту — с правом исправления (E2, issue #1100): она ложится в книгу
            исправлений днём отмены, закрытый месяц не трогая. Без права её нет вовсе — молчим о ней. */}
        {can.permission('costs.invoice.pay') && can.permission('costs.invoice.correct') && (
          <> Оплату отменить можно: отмена ляжет в книгу исправлений.</>
        )}
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
  // Отмена оплаты запертого — правом оплаты И правом исправления (E2, issue #1100, spec v3.3 п. 1.5).
  // Без второго кнопки нет вовсе: почему счёт не правится, говорит полоса над формой.
  const canCancel = canPay && (!locked || can.permission('costs.invoice.correct'));

  if (!payment.paid) {
    // У отклонённого оплаты нет вовсе, а не «кнопка и причина отказа»: действие невыполнимо, пока счёт
    // не вернули в черновик, и о состоянии уже говорит чип в шапке.
    if (!canPay || view.requisites[K.state] === REJECTED) return null;

    return (
      <div className="flex items-center gap-2 flex-wrap text-body-small">
        <Button size="sm" variant="tonal" icon={<Icon icon={Payments} size={18} />} disabled={dirty}
          onClick={() => setDialog('pay')}>
          Отметить оплату
        </Button>
        {/* Причина — рядом и видимая: приглушённая кнопка сама о ней молчит. */}
        {dirty && <span className="text-on-surface-variant">сохраните правки счёта: оплата считается по сохранённому</span>}
        {!dirty && payment.refusal && <span className="text-warning">оплатить нельзя: {payment.refusal}</span>}
        {dialog === 'pay' && <PayDialog view={view} onDone={() => setDialog(null)} />}
      </div>
    );
  }

  return (
    <div className="flex items-center gap-x-4 gap-y-1 flex-wrap text-body-small text-on-surface-variant">
      {/* Текст платёжного документа денег не двигает — открыт и запертому (spec v3.3 п. 1.5). */}
      <PaidDocument view={view} editable={canPay} />
      <span>
        Учётный период: <b className="tabular-nums">{payment.periods.join(' + ') || '—'}</b>{' '}
        <button type="button" className="underline decoration-dotted underline-offset-2 text-on-surface-variant"
          onClick={() => setDialog('posting')}>
          расклад
        </button>
      </span>
      {canCancel && (
        <Button size="sm" variant="text" danger icon={<Icon icon={Undo} size={18} />} onClick={() => setDialog('cancel')}>
          Отменить оплату
        </Button>
      )}
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
    <Tooltip content="Дату меняют отменой оплаты и новой отметкой">
      <span>
        Оплачен <b className="tabular-nums">{payment.paidOn ? formatDate(payment.paidOn) : '—'}</b>
        {' · '}платёжный документ: {payment.document ?? 'не указан'}{' '}
        {editable && (
          <button type="button" className="underline decoration-dotted underline-offset-2 text-on-surface-variant"
            onClick={() => setDraft(payment.document ?? '')}>
            изменить
          </button>
        )}
      </span>
    </Tooltip>
  );
}

/**
 * Диалог отметки оплаты.
 *
 * <p>⚠️ Записать можно только по СВЕЖЕМУ раскладу: пока идёт пересчёт после смены даты, кнопка
 * неактивна — иначе человек подтвердил бы расклад прежней даты. Сервер это ловит отметкой расклада,
 * но отказ после нажатия хуже кнопки, которая честно ждёт.</p>
 */
export function PayDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const toast = useToast();
  const preview = usePaymentPreview();
  const pay = usePayInvoice();
  // Первый вопрос — без даты: «сегодня» называет сервер. Пока ответ не пришёл, поле даты пустое — а
  // не сегодняшнее число браузера: его часы могут стоять в другом поясе, чем часы компании.
  const first = useFirstPaymentPreview(view.id);
  const [asked, setAsked] = useState<PaymentPosting | null>(null);
  const [date, setDate] = useState<string | null>(null);
  const [document, setDocument] = useState('');
  // Признак правок — только выбор ЧЕЛОВЕКА. Само состояние `date` сюда не годится (ревью PR #1311):
  // его ставит и ответ сервера на вопрос о раскладе, и окно без единой правки спрашивало бы о потере.
  const [picked, setPicked] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const [changed, setChanged] = useState<ReadonlySet<string>>(new Set());
  const turns = useRef(0);

  const posting = asked ?? first.data ?? null;
  const paidOn = date ?? posting?.paidOn ?? '';
  // Ошибка первого вопроса жива, только пока на него не ответили позже: запрос больше не повторяется,
  // и без этого условия удачный повтор оставлял бы диалог в «расклад не получен» до закрытия.
  const failed = refused ?? (asked === null && first.error ? apiError(first.error, 'сервер не ответил') : null);
  // Дата, вернувшаяся к предложенной сервером, — уже не правка.
  const edited = document.trim() !== '' || (picked !== null && picked !== (first.data?.paidOn ?? ''));

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
        acknowledge="Понятно">
        <p role="alert" className="text-body-medium text-on-surface">
          Оплатить нельзя: {posting.refusal}.
          {posting.refusal.includes('расходится') && ' Исправьте строки или сумму счёта.'}
        </p>
      </Modal>
    );

  return (
    <Modal open wide onOpenChange={o => { if (!o) onDone(); }} title={`Оплата: ${invoiceTitle(view)}`}
      isDirty={edited} busy={pay.isPending}
      footer={
        <div className="flex items-center justify-end gap-2">
          <ModalExit />
          <Button variant="filled" disabled={!payable} loading={pay.isPending} onClick={submit}>
            {posting && payable ? payLabel(posting.rows) : 'Отметить оплату'}
          </Button>
        </div>
      }>
      <div className="space-y-4">
        {error && (
          <p role="alert" className="text-body-medium text-error">
            {error}{changed.size > 0 && ' Расклад обновлён — проверьте перенос.'}
          </p>
        )}

        <div className="grid grid-cols-2 gap-3">
          <DateField label="Дата платежа" value={paidOn} required
            hint={posting?.dateRefusal ?? undefined}
            onChange={iso => {
              const next = iso ? iso.slice(0, 10) : '';
              setPicked(next);
              setDate(next);
              if (next) void ask(next);
            }} />
          <TextField label="Платёжный документ" value={document} hint="необязательно; можно дописать позже"
            onChange={e => setDocument(e.target.value)} />
        </div>

        {posting?.total != null && (
          <p className="text-body-medium text-on-surface-variant">К оплате <b className="tabular-nums">{formatMoney(posting.total)}</b></p>
        )}

        <div>
          <h3 className="text-body-small font-medium text-on-surface-variant mb-1.5">Когда войдёт в затраты</h3>
          {failed
            ? (
              <p role="alert" className="text-body-medium text-error">
                Расклад не получен: {failed}{' '}
                <button type="button" className="underline" onClick={() => void ask(paidOn || null)}>Повторить</button>
              </p>
            )
            : posting === null ? <p className="text-body-medium text-on-surface-variant">Считаем расклад…</p>
              : posting.dateRefusal ? <p className="text-body-medium text-warning">Оплатить этой датой нельзя: {posting.dateRefusal}.</p>
                : (
                  <div className={fresh ? '' : 'opacity-50'} aria-busy={!fresh}>
                    {!fresh && <p className="text-body-medium text-on-surface-variant mb-1">пересчитываем…</p>}
                    <PaymentPostingTable key={posting.stamp} posting={posting} changed={changed} />
                  </div>
                )}
        </div>
      </div>
    </Modal>
  );
}

/** Отмена ошибочной отметки — с причиной: версий оплата не создаёт, и другого следа не остаётся. */
export function CancelDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const toast = useToast();
  const cancel = useCancelPayment();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Отменить оплату: ${invoiceTitle(view)}`}
      busy={cancel.isPending}
      footer={
        <div className="flex items-center justify-end gap-2">
          <ModalExit>Не отменять</ModalExit>
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
        <p className="text-body-medium text-on-surface-variant">
          Счёт вернётся в «не оплачен», учётные даты долей сотрутся. Запись об оплате в журнале действий
          остаётся: отмена записывается рядом, с причиной.
        </p>
        {view.payment.lockedBy !== null && (
          <p className="text-body-medium text-on-surface-variant">
            Счёт заперт: {view.payment.lockedBy}. Закрытый месяц отмена не изменит — она ляжет в книгу
            исправлений днём отмены. Удалить счёт после этого будет нельзя, только отклонить.
          </p>
        )}
        <TextAreaField label="Причина отмены" value={reason} onChange={e => setReason(e.target.value)}
          rows={3} required
          hint={view.payment.lockedBy !== null ? 'её увидят в журнале действий и в книге исправлений' : 'её увидят в журнале действий'} />
        {error && <p role="alert" className="text-body-medium text-error">{error}</p>}
      </div>
    </Modal>
  );
}

/** Записанный расклад — та же таблица, только чтение. */
function PostedDialog({ view, onDone }: { view: InvoiceView; onDone: () => void }) {
  const { data, error, isLoading } = usePostedPayment(view.id, true);

  return (
    <Modal open wide nothingToLose onOpenChange={o => { if (!o) onDone(); }} title={`Расклад оплаты: ${invoiceTitle(view)}`}
      footer={<div className="flex justify-end"><ModalExit>Закрыть</ModalExit></div>}>
      {isLoading ? <p className="text-body-medium text-on-surface-variant">Читаем расклад…</p>
        : error || !data ? <p role="alert" className="text-body-medium text-error">Расклад не получен: {apiError(error, 'сервер не ответил')}</p>
          : <PaymentPostingTable posting={data} expanded />}
    </Modal>
  );
}
