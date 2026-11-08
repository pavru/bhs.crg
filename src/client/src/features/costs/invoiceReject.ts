import type { InvoiceView } from '@/shared/api/invoices';
import { isConflict } from '@/shared/utils/apiError';
import { K, REJECTED } from './invoiceFields';

/** Чем кончилось «Отклонить»: отклонили этим запросом — или счёт уже был отклонён. */
export type RejectOutcome = 'rejected' | 'already';

/**
 * Исход с версией: «Вернуть» (costs-invoice-screen-13-undo, D-13.1) возвращает в черновик ровно тот
 * отклонённый счёт, который вернул сервер. У «уже отклонён» версии нет нарочно: отклонил, возможно, сосед, и
 * возвращать чужое решение одной кнопкой нельзя.
 */
export type RejectResult = { outcome: 'rejected'; version: string } | { outcome: 'already'; version: null };

/** Версия для «Вернуть» — только у отклонённого этим запросом. */
export const undoVersion = (result: RejectResult): string | null => result.version;

/**
 * Отклонить счёт — и отличить «не отклонён» от «уже отклонён» (ревью PR #1290).
 *
 * <p>Отказ 409 здесь бывает про два разных положения, и по самому отказу их не различить. Счёт тем
 * временем изменили, и он по-прежнему не отклонён — это отказ, человек перечитывает и решает заново.
 * Или счёт УЖЕ отклонён: ответ на первый запрос потерялся, либо сосед отклонил его секундой раньше, —
 * и тогда повтор называет прежнюю версию, сервер отвечает тем же 409, а диалог говорил бы «Счёт не
 * отклонён» про отклонённый счёт. Различает их только свежепрочитанный счёт, поэтому после 409 он
 * читается, и «уже отклонён» перестаёт быть отказом: человек хотел именно этого.</p>
 *
 * <p>Сверку версии на сервере ради этого не ослабляли: она одна на все правки счёта, и исключение
 * «состояние уже то» пришлось бы объяснять каждому следующему переходу.</p>
 *
 * @param send сам запрос отклонения; ответ — вид отклонённого счёта.
 * @param readFresh прочитать счёт заново, мимо кэша.
 */
export async function rejectInvoice(
  send: () => Promise<Pick<InvoiceView, 'version'>>, readFresh: () => Promise<InvoiceView>,
): Promise<RejectResult> {
  try {
    const rejected = await send();
    return { outcome: 'rejected', version: rejected.version };
  } catch (refusal) {
    if (!isConflict(refusal)) throw refusal;
    // Не прочитался — остаётся исходный отказ: он про счёт, а сбой чтения про связь.
    const fresh = await readFresh().catch(() => null);
    if (fresh?.requisites[K.state] === REJECTED) return { outcome: 'already', version: null };
    throw refusal;
  }
}
