import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Block, Delete } from '@/shared/ui/icons';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { useFreshInvoice, useInvoiceState, type InvoiceView } from '@/shared/api/invoices';
import { K, REJECTED } from './invoiceFields';
import { rejectInvoice, type RejectOutcome } from './invoiceReject';
import { useInvoiceDelete } from './invoiceDelete';

/**
 * Меню «Ещё» в шапке формы счёта (issue #1284): редкие действия над счётом целиком.
 *
 * <p>Действий два. «Отклонить…» — у счёта, по которому ещё можно передумать платить. «Удалить счёт…»
 * (issue #1282) — под чертой и только у черновика без оплаты: удаление — для заведённого по ошибке и
 * для дубля, а настоящий счёт, по которому не платим, отклоняют. Действие, которое сервер заведомо
 * откажется выполнить, не показывается вовсе: оплаченный счёт сначала лишают оплаты, и кнопка для
 * этого стоит строкой ниже. Меню без пунктов не рисуется — у оплаченного и у отклонённого его нет.</p>
 *
 * <p><b>«Вернуть в черновик» здесь нет</b> (ревью PR #1290): у возврата вход один — кнопка под
 * строками счёта, та же, что возвращает разобранный. Второй вход в меню жил своей мутацией и своими
 * правилами: кнопки не знали о нажатии друг друга, и отказ у них звучал по-разному.</p>
 *
 * <p><b>Несохранённые правки шапки отклонению не мешают.</b> Они лежат поверх вида счёта и переход
 * состояния переживают: «Состояние» человек не правит, и основа черновика переезжает на новую версию
 * молча (см. `useDraftBase`). Запрет «сначала сохраните» стоял здесь по ошибке — и только здесь.</p>
 *
 * <p>Меню, а не кнопка рядом с «Сохранить»: «Отклонить» — решение о документе, а не шаг его
 * заполнения, и рядом с «Разобран» читалось бы как его пара.</p>
 */
export function InvoiceMoreMenu({ view, onRejected }: {
  view: InvoiceView;
  /** Отклонение идёт через страницу (issue #1293): под отбором очереди она ведёт к следующему счёту и
   *  говорит об этом сама, иначе зовёт `say`. Обещание кончается ответом на запись, а не переходом:
   *  диалог не ждёт перечитывания списка, и сбой перехода не читается как «счёт не отклонён». */
  onRejected?: (send: () => Promise<RejectOutcome>, say: (outcome: RejectOutcome) => void) => Promise<RejectOutcome>;
}) {
  const state = useInvoiceState();
  const readFresh = useFreshInvoice();
  const toast = useToast();
  const [asking, setAsking] = useState(false);
  const askDelete = useInvoiceDelete();

  const rejected = view.requisites[K.state] === REJECTED;
  const number = String(view.requisites[K.number] || 'без номера');
  const label = `Счёт № ${number}`;

  const actions: RowAction[] = [];
  if (!rejected && !view.payment.paid)
    actions.push({ key: 'reject', label: 'Отклонить…', icon: <Icon icon={Block} size={18} />, onSelect: () => setAsking(true) });
  if (askDelete)
    actions.push({
      key: 'delete', label: 'Удалить счёт…', icon: <Icon icon={Delete} size={18} />, danger: true,
      onSelect: () => askDelete('menu'),
    });

  // Диалог остаётся смонтированным и без меню: счёт становится отклонённым, пока диалог ещё закрывается.
  return (
    <>
      {actions.length > 0 && <RowActionsMenu actions={actions} ariaLabel="Ещё" />}
      <ConfirmDialog open={asking} onOpenChange={setAsking}
        title={`Отклонить счёт № ${number}?`}
        errorTitle="Счёт не отклонён"
        confirmLabel="Отклонить счёт" confirmDanger={false}
        description={(
          <div className="space-y-2 text-sm text-on-surface-variant">
            <p>По счёту решено не платить. Он <b>останется в реестре</b> с состоянием «Отклонён»:</p>
            <ul className="list-disc pl-5 space-y-1">
              <li>в затраты, в «К оплате» и в сроки оплаты не входит;</li>
              <li>отметить оплату по нему нельзя;</li>
              <li>строки, разноска и файл остаются как есть.</li>
            </ul>
            <p>Передумали — «Вернуть в черновик» под строками счёта.</p>
          </div>
        )}
        onConfirm={() => {
          const send = () => rejectInvoice(
            () => state.mutateAsync({ id: view.id, seen: view.version, to: 'rejected' }),
            () => readFresh(view.id),
          );
          // «Уже отклонён» ведёт так же: счёт из очереди вышел, и человек хотел именно этого.
          const say = (outcome: RejectOutcome) => (outcome === 'rejected'
            ? toast.success(`${label} отклонён.`)
            : toast.info(`${label} уже отклонён.`));
          return onRejected ? onRejected(send, say) : send().then(say);
        }} />
    </>
  );
}
