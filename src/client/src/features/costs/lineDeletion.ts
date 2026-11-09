import { formatMoney, formatQuantityUnit } from '@/shared/format/format';
import type { AllocationPartView, InvoiceLineView } from '@/shared/api/invoices';
import { issueOf, issueText } from './lostReferences';

/** Цель части разноски словами — как в матрице: стройка и раздел либо статья вне строек. */
export interface CascadeTarget {
  target: string;
  /** Раздел, «Статья вне строек» или что не так с целью; пусто — стройка в целом. */
  detail: string;
  /** С целью что-то не так (`targetIssue`) — подпись тоном ошибки. */
  troubled: boolean;
  /** Доля — количеством строки, у строки без количества — суммой. */
  share: string;
  /** Сумма части у строки с количеством; у строки суммой сумма и есть доля — пусто. */
  amount: string;
}

/**
 * Что удалится вместе со строкой (#1655, вариант Б): каждая часть сохранённой разноски словами. Числа — сервера:
 * удаляется записанное, а не черновик матрицы.
 */
export function cascadeTargets(saved: InvoiceLineView): CascadeTarget[] {
  const byQuantity = saved.allocation.mode !== 'amount';
  return saved.allocation.parts.map((part: AllocationPartView) => {
    const issue = issueOf(part);
    const article = part.articleId !== null;
    return {
      target: (article ? part.articleName : part.constructionName) ?? '—',
      detail: issue ? issueText(issue) : article ? 'Статья вне строек' : part.sectionName ?? '',
      troubled: issue !== null,
      share: byQuantity
        ? part.quantity === null ? '—' : formatQuantityUnit(part.quantity, saved.unit)
        : part.amount === null ? '—' : formatMoney(part.amount),
      amount: byQuantity && part.amount !== null ? formatMoney(part.amount) : '',
    };
  });
}

/** Итог строки над перечнем: «100 м · 18 400,00 ₽» — чтобы «та ли это строка» было видно сразу. */
export function lineTotal(saved: InvoiceLineView): string {
  const money = saved.amount === null ? null : formatMoney(saved.amount);
  const quantity = saved.quantity === null ? 'Без количества' : formatQuantityUnit(saved.quantity, saved.unit);
  return money === null ? quantity : `${quantity} · ${money}`;
}
