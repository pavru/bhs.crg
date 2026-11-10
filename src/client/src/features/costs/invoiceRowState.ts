import type { InvoiceListItem } from '@/shared/api/invoices';
import { Block, CheckCircle, Draft, Help, Payments, type IconDef } from '@/shared/ui/icons';
import { DRAFT, PAID, PARSED, REJECTED } from './invoiceFields';

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
 *
 * <p>Номера нет, а скан есть — подпись строки берёт имя файла (`headlineByFile`). Что это файл, а не номер,
 * подсказка говорит при ЛЮБОМ состоянии: отклонить и оплатить можно и черновик без номера (ревью PR #1860), а
 * прежний знак скана у подписи макет А снял.</p>
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

/** Подпись строки — имя файла: номера нет, скан есть. Иначе три черновика из скана подряд неразличимы. */
export function headlineByFile(item: Pick<InvoiceListItem, 'number' | 'scanFileName'>): boolean {
  return !item.number && !!item.scanFileName;
}

/** Строка подсказки о подписи-имени файла. */
export const BY_FILE_HINT = 'Номера ещё нет — в подписи имя файла';

export function rowState(item: Pick<InvoiceListItem, 'state' | 'payment' | 'number' | 'scanFileName'>): RowState {
  const state = bareState(item);
  return headlineByFile(item) ? { ...state, hint: `${state.hint}
${BY_FILE_HINT}` } : state;
}

function bareState(item: Pick<InvoiceListItem, 'state' | 'payment'>): RowState {
  if (item.state === REJECTED) return { icon: Block, word: 'отклонён', hint: 'Отклонён — по счёту не платим' };
  if (item.payment === PAID) {
    return item.state === PARSED
      ? { icon: Payments, tone: 'text-success', word: 'оплачен', hint: 'Оплачен' }
      : { icon: Payments, tone: 'text-success', word: 'оплачен, черновик', hint: 'Оплачен, но не разобран — черновик' };
  }
  if (item.state === PARSED) return { icon: CheckCircle, tone: 'text-primary', word: 'разобран', hint: 'Разобран, не оплачен' };
  if (item.state === DRAFT) return { icon: Draft, word: 'черновик', hint: 'Черновик — ещё не разобран' };
  const word = item.state.toLowerCase();
  return { icon: Help, word, hint: `Состояние «${item.state}»` };
}
