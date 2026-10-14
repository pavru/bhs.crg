import type { CostsOrganization } from '@/shared/api/invoices';
import type { SupplierTermsItem } from '@/shared/api/supplierTerms';

/** Пределы отсрочки — те же, что у поля счёта на сервере: полдня и век — опечатка, а не условие. */
export const MAX_DEFERRAL = 3650;

/**
 * Набранная отсрочка: число дней, пусто (`null`) либо `'bad'` — набрано не то. Отказ называется у поля,
 * до запроса: сервер ответил бы тем же, но после нажатия и общим сообщением.
 */
export function parseDays(text: string): number | null | 'bad' {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  if (!/^\d+$/.test(trimmed)) return 'bad';
  const days = Number(trimmed);
  return days > MAX_DEFERRAL ? 'bad' : days;
}

export const BAD_DAYS = `Целое число дней от 0 до ${MAX_DEFERRAL}. Ноль — платим в день отгрузки`;

/**
 * Кого можно выбрать в новые условия: действующие организации, у которых условий ещё нет. Поставщик с
 * условиями правится своей строкой — второй записи на него сервер не примет.
 */
export function choosable(organizations: readonly CostsOrganization[], terms: readonly SupplierTermsItem[]): CostsOrganization[] {
  const taken = new Set(terms.map(t => t.supplierId));
  return organizations.filter(o => !o.archived && !taken.has(o.id));
}

/** Чем назвать поставщика строки: удалённый — словами, а не пустотой. */
export function termsSupplier(item: Pick<SupplierTermsItem, 'supplierName' | 'supplierLost'>): string {
  if (item.supplierLost) return 'поставщик удалён';
  return item.supplierName?.trim() || 'поставщик без названия';
}

/** Вопрос перед снятием условий — что изменится и чего не изменится. */
export function removeQuestion(item: SupplierTermsItem): string {
  return `Отсрочка ${item.deferralDays} дн. у поставщика «${termsSupplier(item)}» перестанет подставляться ` +
    'в новые счета. В уже заведённых счетах отсрочка и срок останутся как есть.';
}
