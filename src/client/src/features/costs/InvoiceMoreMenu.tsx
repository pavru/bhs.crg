import { useState } from 'react';
import { Ban, Undo2 } from 'lucide-react';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { useInvoiceState, type InvoiceView } from '@/shared/api/invoices';
import { K, REJECTED } from './invoiceFields';

/**
 * Меню «Ещё» в шапке формы счёта (issue #1284): редкие действия над счётом целиком.
 *
 * <p>Сегодня их два, и видно всегда не больше одного: «Отклонить…» у счёта, по которому ещё можно
 * передумать платить, и «Вернуть в черновик» у отклонённого. Действие, которое сервер заведомо
 * откажется выполнить, не показывается вовсе — оплаченный счёт сначала лишают оплаты, и кнопка для
 * этого стоит строкой ниже. Меню без пунктов не рисуется.</p>
 *
 * <p>Меню, а не кнопка рядом с «Сохранить»: «Отклонить» — решение о документе, а не шаг его
 * заполнения, и рядом с «Разобран» читалось бы как его пара.</p>
 */
export function InvoiceMoreMenu({ view, dirty }: {
  view: InvoiceView;
  /** В шапке есть несохранённые правки: переход перечитал бы счёт и стёр их молча. */
  dirty: boolean;
}) {
  const state = useInvoiceState();
  const toast = useToast();
  const [asking, setAsking] = useState(false);

  const rejected = view.requisites[K.state] === REJECTED;
  const number = String(view.requisites[K.number] || 'без номера');
  const label = `Счёт № ${number}`;

  const actions: RowAction[] = [];
  if (rejected) {
    actions.push({
      key: 'draft', label: 'Вернуть в черновик', icon: <Undo2 size={14} />,
      onSelect: () => {
        if (dirty) { toast.info('Сначала сохраните правки счёта — возврат перечитает его.'); return; }
        state.mutateAsync({ id: view.id, seen: view.version, to: 'draft' })
          .then(() => toast.success(`${label} возвращён в черновик.`))
          .catch(e => toast.apiError(e, 'Счёт не возвращён в черновик'));
      },
    });
  } else if (!view.payment.paid) {
    actions.push({ key: 'reject', label: 'Отклонить…', icon: <Ban size={14} />, onSelect: () => setAsking(true) });
  }

  if (actions.length === 0) return null;

  return (
    <>
      <RowActionsMenu actions={actions} ariaLabel="Ещё" />
      <ConfirmDialog open={asking} onOpenChange={setAsking}
        title={`Отклонить счёт № ${number}?`}
        errorTitle="Счёт не отклонён"
        confirmLabel="Отклонить счёт" confirmDanger={false}
        blocked={dirty ? 'В счёте есть несохранённые правки. Сохраните их — отклонение перечитает счёт.' : undefined}
        description={(
          <div className="space-y-2 text-sm text-fg2">
            <p>По счёту решено не платить. Он <b>останется в реестре</b> с состоянием «Отклонён»:</p>
            <ul className="list-disc pl-5 space-y-1">
              <li>в затраты, в «К оплате» и в сроки оплаты не входит;</li>
              <li>отметить оплату по нему нельзя;</li>
              <li>строки, разноска и файл остаются как есть.</li>
            </ul>
            <p>Передумали — «Вернуть в черновик» в этом же меню.</p>
          </div>
        )}
        onConfirm={() => state.mutateAsync({ id: view.id, seen: view.version, to: 'rejected' })
          .then(() => toast.success(`${label} отклонён.`))} />
    </>
  );
}
