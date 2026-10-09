import type { SupplierMatchItem } from '@/shared/api/supplierMatches';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Слова списка соответствий (задача C3, issue #1079) — отдельно от окна, чтобы их можно было проверить.
 */

/** Чем назвать поставщика. Удалённый — не «без названия»: это разные беды, и чинятся они по-разному. */
export function supplierLabel(supplier: { name: string | null; lost: boolean }): string {
  if (supplier.lost) return 'поставщик удалён';
  return supplier.name?.trim() || 'поставщик без названия';
}

/** Почему соответствие не подставляется — строкой под позицией; `null` — подставляется. */
export function issueNote(issue: SupplierMatchItem['issue']): string | null {
  if (issue === 'archived') return 'в архиве — не подставляется';
  if (issue === 'lost') return 'позиция удалена — не подставляется';
  return null;
}

/**
 * «Показано N из M». Число «из» обязательно: без него порция читается как весь список, и человек,
 * не найдя соответствия глазами, решит, что его нет.
 */
export function shownOf(shown: number, total: number): string {
  return shown >= total
    ? `Всего: ${total} ${ruPlural(total, 'соответствие', 'соответствия', 'соответствий')}`
    : `Показано ${shown} из ${total}`;
}

/** Что именно забывают и что из этого следует — текст вопроса перед «Забыть». */
export function forgetQuestion(item: SupplierMatchItem): string {
  const key = `${item.by === 'code' ? 'артикул' : 'наименование'} «${item.source}»`;
  const position = item.issue === 'lost' ? 'удалённая позиция' : `«${item.nomenclatureName ?? 'позиция без названия'}»`;
  return `${supplierLabel({ name: item.supplierName, lost: item.supplierLost })}: ${key} → ${position}. `
    + 'В следующих счетах такая строка будет ждать выбора. В уже сохранённых счетах позиция останется.';
}
