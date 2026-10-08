import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { QK } from './invoices';

/** Числа для чипов «наведите порядок» над списком счетов (issue #1186). */
export interface InvoiceQueues {
  /** Счетов с удалённой записью, которые можно исправить, — столько строк под отбором. */
  lost: number;
  /** Неоплаченных счетов с записью из архива. */
  archived: number;
  /** Счетов с удалённой записью в закрытом периоде: в `lost` не входят, исправить их нельзя. */
  locked: number;
  /** Почему числам нельзя верить как полным. ⚠️ Ноль с этой причиной — не «счетов нет». */
  doubt: string | null;
  /** Черновиков со сканом без строк, чей скан сейчас не читается (issue #1077). */
  unrecognized: number;
}

/**
 * Числа чипов — одним ответом и одним опросом сервера: сколько исправить, сколько в архиве, сколько
 * заперто. Те же числа стоят над реестром счетов; равенство сторожит тест сервера.
 *
 * Ключ — под ключом счетов, свежесть — как у списка (`useInvoices`): правка счёта сбрасывает оба, и
 * возврат на экран перечитывает оба. Разная свежесть давала бы «4» на чипе над тремя счетами.
 *
 * @param enabled спрашивать ли: числа нужны тому, кто счёт может исправить.
 */
export function useInvoiceQueues(enabled: boolean) {
  return useQuery({
    queryKey: [QK, 'queues'] as const,
    queryFn: () => apiClient.get<InvoiceQueues>('/costs/invoices/queues').then(r => r.data),
    enabled,
    retry: false,
    staleTime: 0,
  });
}
