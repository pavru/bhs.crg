import { useState } from 'react';
import { Block, Delete, Undo } from '@/shared/ui/icons';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useFreshInvoice, useInvoiceState, type InvoiceView } from '@/shared/api/invoices';
import { K, PARSED, REJECTED } from './invoiceFields';
import { rejectInvoice, type RejectOutcome } from './invoiceReject';
import { useInvoiceDelete } from './invoiceDelete';

/**
 * «⋮» сводки счёта (issue #1284; costs-invoice-screen-10-layout, D-10.6): действия над счётом целиком,
 * которые не основное действие. Порядок: файл («Приложить/Заменить файл…», «Распознать файл»), переходы
 * («Вернуть в черновик», «Отклонить…»), «Исправить», под чертой — «Удалить счёт…».
 *
 * <p>«Удалить счёт…» (issue #1282) — только у черновика без оплаты и без книги исправлений: удаление —
 * для заведённого по ошибке и для дубля, а настоящий счёт, по которому не платим, отклоняют. Действие,
 * которое сервер заведомо откажется выполнить, не показывается вовсе. Меню без пунктов не рисуется.</p>
 *
 * <p><b>«Вернуть в черновик»</b> — у разобранного и отклонённого: оба перехода назад отсюда, одной мутацией
 * (ревью PR #1290: второй вход жил своей мутацией и своими правилами). Возврат отклонённого — решение о
 * счёте, как и отклонение: о нём говорят вслух.</p>
 *
 * <p><b>Несохранённые правки шапки переходам не мешают.</b> Они лежат поверх вида счёта и переход
 * состояния переживают: «Состояние» человек не правит, и основа черновика переезжает на новую версию
 * молча (см. `useDraftBase`).</p>
 */
export function InvoiceMoreMenu({ view, canChange, file, recognize, correct, page = [], onRejected }: {
  view: InvoiceView;
  /** Переходы состояния доступны: счёт не заперт, не в исправлении, право вводить счета есть. */
  canChange: boolean;
  /** «Приложить/Заменить файл…» — `null`, когда менять файл нельзя. */
  file?: RowAction | null;
  /** «Распознать файл» — `null`, когда распознавать нечего. */
  recognize?: RowAction | null;
  /** «Исправить» — у запертого счёта тому, у кого есть право исправления. */
  correct?: RowAction | null;
  /** Действия страницы, когда списка нет (D-10.8): «Новый счёт» и справочники — после действий над счётом. */
  page?: RowAction[];
  /** Отклонение идёт через страницу (issue #1293): под отбором очереди она ведёт к следующему счёту и
   *  говорит об этом сама, иначе зовёт `say`. Обещание кончается ответом на запись, а не переходом:
   *  диалог не ждёт перечитывания списка, и сбой перехода не читается как «счёт не отклонён». */
  onRejected?: (send: () => Promise<RejectOutcome>, say: (outcome: RejectOutcome) => void) => Promise<RejectOutcome>;
}) {
  const state = useInvoiceState();
  const readFresh = useFreshInvoice();
  const toast = useSnackbar();
  const [asking, setAsking] = useState(false);
  const askDelete = useInvoiceDelete();

  const rejected = view.requisites[K.state] === REJECTED;
  const parsed = view.requisites[K.state] === PARSED;
  const number = String(view.requisites[K.number] || 'без номера');
  const label = `Счёт № ${number}`;

  async function toDraft() {
    try {
      await state.mutateAsync({ id: view.id, seen: view.version, to: 'draft' });
      if (rejected) toast.success('Счёт возвращён в черновик.');
    } catch (e) { toast.apiError(e, 'Счёт не возвращён в черновик'); }
  }

  const actions: RowAction[] = [];
  if (file) actions.push(file);
  if (recognize) actions.push(recognize);
  if (canChange && (parsed || rejected))
    actions.push({ key: 'draft', label: 'Вернуть в черновик', icon: Undo, loading: state.isPending, onSelect: () => void toDraft() });
  if (canChange && !rejected && !view.payment.paid)
    actions.push({ key: 'reject', label: 'Отклонить…', icon: Block, onSelect: () => setAsking(true) });
  if (correct) actions.push(correct);
  actions.push(...page);
  if (askDelete)
    actions.push({
      key: 'delete', label: 'Удалить счёт…', icon: Delete, danger: true,
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
          <div className="space-y-2 text-body-medium text-on-surface-variant">
            <p>По счёту решено не платить. Он <b>останется в реестре</b> с состоянием «Отклонён»:</p>
            <ul className="list-disc pl-5 space-y-1">
              <li>в затраты, в «К оплате» и в сроки оплаты не входит;</li>
              <li>отметить оплату по нему нельзя;</li>
              <li>строки, разноска и файл остаются как есть.</li>
            </ul>
            <p>Передумали — «Вернуть в черновик» в меню «⋮» счёта.</p>
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
