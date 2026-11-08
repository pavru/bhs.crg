import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from '@/shared/ui/Snackbar';
import {
  INVOICES_KEY, forgetInvoice, sendInvoiceDelete, sendInvoiceDeleteOnLeave, useFreshInvoice, useInvoiceState,
  type InvoiceView,
} from '@/shared/api/invoices';
import { deleteDraftLater, forgetFresh, watchFreshness } from './invoiceDelete';
import { schedule, settle, undo, type DeletePort } from './pendingDelete';
import { forgetCreated } from './scanBatch';

/**
 * «Вернуть» страницы счетов (costs-invoice-screen-13-undo, ТЗ COST-6.6): у отклонения — обратный переход,
 * у нового черновика — отложенное удаление. Живёт на странице, а не в форме: под отбором очереди форма к
 * мигу «Вернуть» показывает уже другой счёт.
 *
 * @param open открыть счёт после «Вернуть»: через охрану ухода, а со страницы ушли — адресом.
 */
export function useInvoiceUndo(open: (id: string) => void) {
  const qc = useQueryClient();
  const toast = useSnackbar();
  const state = useInvoiceState();
  const readFresh = useFreshInvoice();
  // Признак «свежего» черновика снимает любая своя запись счёта (D-13.3) — подписка одна на клиент запросов.
  useEffect(() => watchFreshness(qc), [qc]);

  /**
   * «Вернуть» после «Отклонить» (D-13.1): в черновик с версией из ответа отклонения и к этому счёту. Отказ —
   * словами сервера: счёт остаётся отклонённым.
   */
  const undoReject = (id: string, version: string) => () => {
    state.mutateAsync({ id, seen: version, to: 'draft' })
      .then(() => open(id), e => toast.apiError(e, 'Счёт не возвращён в черновик'));
  };

  const gone = (id: string) => { forgetFresh(id); forgetCreated(id); forgetInvoice(qc, id); };
  // Разбор отказов — общий с диалогом (`deleteRecord`): 404 — «уже удалён», 409 — свежепрочитанный счёт решает,
  // что сказать, а без ответа исход неизвестен, и «не удалён» было бы неправдой (ревью PR #1808).
  const port: DeletePort = {
    send: (id, version) => deleteDraftLater(() => sendInvoiceDelete(id, version), () => readFresh(id)),
    deleted: (id, outcome) => {
      gone(id);
      // Удалил сосед — человек хотел именно этого: счёт уходит из списка, а не возвращается в него.
      if (outcome === 'already') toast.info('Черновик уже удалён.');
    },
    beacon: sendInvoiceDeleteOnLeave,
    reread: id => forgetInvoice(qc, id),
    refused: (_id, error) => {
      // Автоповтора нет (D-13.4): удалять то, чего человек не видел, нельзя. Список — заново: без ответа
      // черновик мог и уйти, а изменённый должен встать строкой уже свежим.
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
      toast.apiError(error, 'Черновик не удалён');
    },
  };

  /**
   * Удалить новый черновик без окна (D-13.4): черновик скрыт сразу, `DELETE` уходит по закрытию снэкбара.
   * Срок «Вернуть» — `SNACKBAR_UNDO_MS`: уведомление с откликом получает его само.
   *
   * @param message что сказать: под очередью — и какой счёт открыт следующим.
   * @returns `false` — удаление этого черновика уже ждёт (двойной щелчок): ни снэкбара, ни перехода.
   */
  function deferDelete(view: InvoiceView, message: string): boolean {
    if (!schedule(view.id, view.version, port)) return false;
    toast.success(message, {
      action: {
        label: 'Вернуть',
        onClick: () => {
          if (undo(view.id)) open(view.id);
          else toast.info('Черновик не возвращён: удаление уже ушло.');
        },
      },
      onClose: reason => settle(view.id, reason),
    });
    return true;
  }

  return { undoReject, deferDelete };
}
