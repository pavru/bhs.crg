import type { InvoiceView } from '@/shared/api/invoices';
import { K, REJECTED } from './invoiceFields';

/** Чем кончилось «Отклонить»: отклонили этим запросом — или счёт уже был отклонён. */
export type RejectOutcome = 'rejected' | 'already';

const isConflict = (error: unknown) => (error as { response?: { status?: number } })?.response?.status === 409;

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
 * @param send сам запрос отклонения.
 * @param readFresh прочитать счёт заново, мимо кэша.
 */
export async function rejectInvoice(
  send: () => Promise<unknown>, readFresh: () => Promise<InvoiceView>,
): Promise<RejectOutcome> {
  try {
    await send();
    return 'rejected';
  } catch (refusal) {
    if (!isConflict(refusal)) throw refusal;
    // Не прочитался — остаётся исходный отказ: он про счёт, а сбой чтения про связь.
    const fresh = await readFresh().catch(() => null);
    if (fresh?.requisites[K.state] === REJECTED) return 'already';
    throw refusal;
  }
}
