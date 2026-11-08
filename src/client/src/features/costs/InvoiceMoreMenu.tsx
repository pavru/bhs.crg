import { Block, Delete, Undo } from '@/shared/ui/icons';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { SNACKBAR_UNDO_MS, useSnackbar } from '@/shared/ui/Snackbar';
import { useFreshInvoice, useInvoiceState, type InvoiceView } from '@/shared/api/invoices';
import { K, PARSED, REJECTED } from './invoiceFields';
import { rejectInvoice, type RejectResult } from './invoiceReject';
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
 * <p><b>«Отклонить» — без окна</b> (costs-invoice-screen-13-undo, D-13.1, ТЗ COST-6.6): у перехода есть
 * обратный, и честнее отмена, чем вопрос. Пояснение из прежнего окна — строкой под пунктом; после ответа —
 * снэкбар «Счёт № … отклонён · Вернуть». «Вернуть» строит страница: форма под отбором очереди к тому мигу
 * показывает уже следующий счёт, и это меню размонтировано.</p>
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
   *  говорит об этом сама, иначе зовёт `say` — с «Вернуть», который построила (D-13.1). Обещание кончается
   *  ответом на запись, а не переходом: сбой перехода не читается как «счёт не отклонён». */
  onRejected?: (
    send: () => Promise<RejectResult>, say: (result: RejectResult, undo?: () => void) => void,
  ) => Promise<RejectResult>;
}) {
  const state = useInvoiceState();
  const readFresh = useFreshInvoice();
  const toast = useSnackbar();
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

  async function reject() {
    const send = () => rejectInvoice(
      () => state.mutateAsync({ id: view.id, seen: view.version, to: 'rejected' }),
      () => readFresh(view.id),
    );
    // «Уже отклонён» ведёт так же: счёт из очереди вышел, и человек хотел именно этого. «Вернуть» у него нет.
    const say = (result: RejectResult, undo?: () => void) => (result.outcome === 'rejected'
      ? toast.success(`${label} отклонён.`, undo && { duration: SNACKBAR_UNDO_MS, action: { label: 'Вернуть', onClick: undo } })
      : toast.info(`${label} уже отклонён.`));
    try { await (onRejected ? onRejected(send, say) : send().then(result => say(result))); }
    catch (e) { toast.apiError(e, 'Счёт не отклонён'); }
  }

  const actions: RowAction[] = [];
  if (file) actions.push(file);
  if (recognize) actions.push(recognize);
  if (canChange && (parsed || rejected))
    actions.push({ key: 'draft', label: 'Вернуть в черновик', icon: Undo, loading: state.isPending, onSelect: () => void toDraft() });
  if (canChange && !rejected && !view.payment.paid)
    actions.push({
      key: 'reject', label: 'Отклонить', icon: Block, loading: state.isPending,
      hint: 'Не платим: счёт останется в реестре. Передумали — «Вернуть в черновик»',
      onSelect: () => void reject(),
    });
  if (correct) actions.push(correct);
  actions.push(...page);
  // Новый черновик удаляется без окна, и многоточия у пункта нет (D-13.5).
  if (askDelete)
    actions.push({
      key: 'delete', label: askDelete.now ? 'Удалить черновик' : 'Удалить счёт…', icon: Delete, danger: true,
      onSelect: () => askDelete.ask('menu'),
    });

  return actions.length > 0 ? <RowActionsMenu actions={actions} ariaLabel="Ещё" /> : null;
}
