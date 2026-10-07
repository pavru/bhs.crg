import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { QK } from './invoices';
import type { TableShortcut } from './tables';

/** Таблица счетов: её готовые отборы считают числа для чипов списка. */
const INVOICES_TABLE = 'costs.invoices';

/**
 * Числа для отборов «наведите порядок» в списке счетов (issue #1186) — готовые отборы таблицы счетов,
 * те же, что стоят чипами над реестром: число у списка и у реестра обязано быть одно.
 *
 * Ключ — под ключом счетов: любая правка счёта сбрасывает его вместе со списком, и число на чипе
 * уменьшается сразу после замены значения, а не при следующем заходе на экран.
 *
 * Тому, кто счёт править не может, сервер отборов не предлагает — приходит пустой список.
 */
export function useInvoiceQueueCounts() {
  return useQuery({
    queryKey: [QK, 'queues'] as const,
    queryFn: () => apiClient
      .get<TableShortcut[]>(`/tables/${encodeURIComponent(INVOICES_TABLE)}/shortcuts`).then(r => r.data),
    retry: false,
    staleTime: 0,
  });
}

/** Счётчик потерянных ссылок модуля. Здесь нужна одна его часть — счета закрытого периода. */
export interface LostReferences {
  editable: { references: number; invoices: number; waybills: number; other: number };
  locked: { references: number; invoices: number; waybills: number; other: number };
  unfixable: number;
  unchecked: { what: string; reason: string }[];
  asOf: string;
}

/**
 * Сколько счетов с удалёнными записями заперто закрытым периодом: в отбор «удалённые записи» они не
 * входят, и промолчать о них значило бы сказать «больше нет». Спрашиваем только под этим отбором.
 */
export function useLostReferences(enabled: boolean) {
  return useQuery({
    queryKey: [QK, 'lost-references'] as const,
    queryFn: () => apiClient.get<LostReferences>('/costs/lost-references').then(r => r.data),
    enabled,
    retry: false,
    staleTime: 0,
  });
}
