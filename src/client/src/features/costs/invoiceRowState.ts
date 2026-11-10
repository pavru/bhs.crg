import type { InvoiceListItem } from '@/shared/api/invoices';
import { Block, CheckCircle, Draft, Help, Payments, type IconDef } from '@/shared/ui/icons';
import { PAID, PARSED, REJECTED } from './invoiceFields';
import { DRAFT } from './invoiceDelete';

/**
 * Состояние счёта в строке ленты (costs-invoice-list-state-mark, [COST-6.3], макет А): ведущий значок, его
 * тон и слово. Чистая функция — строка только рисует.
 *
 * <p>Старшинство: отклонён → оплачен → разобран → черновик. Оплаченный черновик — значок «оплачен»: деньги
 * ушли, это и важнее всего, а пятый значок пришлось бы учить. Что он ещё черновик, говорит слово.</p>
 *
 * <p>Тон — класс цвета роли или `undefined`: тихий значок берёт тон строки (`ListRowBody`), на выбранной —
 * `on-secondary-container`. Громкие свой цвет держат и там: контраст `success` и `primary` на
 * `secondary-container` не ниже 4.9 в обеих темах (разбор Дизайнера, §79). Разобранный — `primary`, а не
 * жёлтый `warning`: тот в этой же строке значит «проверьте» и одной светлоты с `success` (выбор владельца,
 * 10.11.2026).</p>
 *
 * <p>Состояние, которого клиент не знает (сервер новее), — не черновик: значок вопроса и слово сервера.
 * Иначе новое состояние молча выглядело бы самым ранним.</p>
 */
export interface RowState {
  icon: IconDef;
  /** Класс цвета роли; без него значок тихий. */
  tone?: string;
  /** Слово для имени строки: «оплачен, черновик». */
  word: string;
  /** Подсказка значка — фразой. */
  hint: string;
}

export function rowState(item: Pick<InvoiceListItem, 'state' | 'payment' | 'number' | 'scanFileName'>): RowState {
  if (item.state === REJECTED) return { icon: Block, word: 'отклонён', hint: 'Отклонён — по счёту не платим' };
  if (item.payment === PAID) {
    return item.state === PARSED
      ? { icon: Payments, tone: 'text-success', word: 'оплачен', hint: 'Оплачен' }
      : { icon: Payments, tone: 'text-success', word: 'оплачен, черновик', hint: 'Оплачен, но не разобран — черновик' };
  }
  if (item.state === PARSED) return { icon: CheckCircle, tone: 'text-primary', word: 'разобран', hint: 'Разобран, не оплачен' };
  if (item.state === DRAFT) {
    const byFile = !item.number && !!item.scanFileName;
    return { icon: Draft, word: 'черновик', hint: byFile ? 'Черновик — номера ещё нет, это имя файла' : 'Черновик — ещё не разобран' };
  }
  const word = item.state.toLowerCase();
  return { icon: Help, word, hint: `Состояние «${item.state}»` };
}
