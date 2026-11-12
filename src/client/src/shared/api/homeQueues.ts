import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

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
 * Ключ — оболочки, а не модуля: очереди объявляет любой модуль, и под ключом счетов чужие числа не
 * сбрасывались бы правками своего модуля, а `isInvoiceView` принимал бы Главную за вид счёта. Свежесть —
 * `staleTime: 0`: Главная и экран очереди одновременно не открыты, и каждый заход на Главную перечитывает
 * числа, — «3» после разобранного третьего счёта не остаётся.
 */
export const HOME_QUEUES_KEY = ['home', 'queues'] as const;

export function useHomeQueues() {
  return useQuery({
    queryKey: HOME_QUEUES_KEY,
    queryFn: () => apiClient.get<HomeQueue[]>('/home/queues').then(r => r.data),
    retry: false,
    staleTime: 0,
  });
}
