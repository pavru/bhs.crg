import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { QK as INVOICES } from './invoices';

/** Карточка очереди Главной (shell-08, ТЗ UI-34) — как её отдаёт `GET /api/home/queues`. */
export interface HomeQueue {
  /** Код модуля — вместе с `id` адрес экрана очереди (`homeQueueRoutes.ts`). */
  module: string;
  moduleTitle: string;
  id: string;
  label: string;
  hint: string;
  /** Очередь не зовёт к срочному действию — карточка тише соседних. */
  quiet: boolean;
  /** Сколько; `null` — не посчитано, и тогда назван `refused`. */
  count: number | null;
  /** Почему числу нельзя верить как полному. ⚠️ Ноль с ней — не «пусто». */
  doubt: string | null;
  refused: string | null;
}

/**
 * Очереди Главной — открытые человеку очереди включённых модулей с числами. Права проверяет сервер:
 * чего не открыто, того в ответе нет, и клиент второй раз прав не считает.
 *
 * Ключ — под ключом счетов: правка счёта сбрасывает и список, и числа чипов, и карточки Главной — иначе
 * Главная показывала бы «3» после того, как человек разобрал третий счёт.
 */
export function useHomeQueues() {
  return useQuery({
    queryKey: [INVOICES, 'home'] as const,
    queryFn: () => apiClient.get<HomeQueue[]>('/home/queues').then(r => r.data),
    retry: false,
    staleTime: 0,
  });
}
