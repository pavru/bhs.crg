import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { ACCESS_KEY } from './access';

/**
 * Валюта учёта глазами администратора (issue #1248): что действует, из чего выбирать и можно ли
 * менять. Форматтер сумм читает валюту НЕ отсюда — из ответа о доступе, который получает каждый
 * вошедший; этот адрес открыт только тому, кто настройку меняет.
 */
export interface AccountingCurrencyView {
  /** Действующая валюта — код из списка. */
  currency: string;
  /** Что лежит в базе и в список не входит; `null` — такого нет. Действует при этом рубль. */
  stale: string | null;
  /** Список держит сервер: второй список здесь разошёлся бы с ним молча. */
  currencies: { code: string; title: string }[];
  /** Документы с суммами уже есть — сменить нельзя. */
  locked: boolean;
  /** Почему нельзя — словами сервера, с названием модуля и числом документов. */
  reason: string | null;
}

export const ACCOUNTING_CURRENCY_KEY = ['settings', 'company', 'currency'] as const;

export function useAccountingCurrency() {
  return useQuery<AccountingCurrencyView>({
    queryKey: ACCOUNTING_CURRENCY_KEY,
    queryFn: () => apiClient.get('/settings/company/currency').then(r => r.data),
  });
}

export function useSaveAccountingCurrency() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (currency: string) => apiClient.put('/settings/company/currency', { currency }),
    // И после отказа: 409 означает, что документы появились, пока форма была открыта, — запертое
    // поле обязано прийти с сервера. Ответ о доступе несёт валюту форматтеру: без его обновления
    // суммы рисовались бы прежним знаком до истечения его срока.
    onSettled: () => Promise.all([
      qc.invalidateQueries({ queryKey: ACCOUNTING_CURRENCY_KEY }),
      qc.invalidateQueries({ queryKey: ACCESS_KEY }),
    ]),
  });
}
