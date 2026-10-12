import type { AccountingCurrencyView } from '@/shared/api/accountingCurrency';

/**
 * Валюта словами: «Казахстанский тенге (KZT)». Название — из списка сервера; кода, которого в
 * списке нет, не бывает (сервер отдаёт действующую валюту только из списка), но показать голый код
 * лучше, чем пустое место, если списки когда-нибудь разойдутся.
 */
export function currencyTitle(view: Pick<AccountingCurrencyView, 'currencies'>, code: string): string {
  const known = view.currencies.find(c => c.code === code);
  return known ? `${known.title} (${known.code})` : code;
}
