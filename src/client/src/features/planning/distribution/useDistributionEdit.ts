import { useCan } from '@/shared/api/access';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';

/**
 * Можно ли здесь править распределение: право `plan.estimate.edit` и широкий экран (телефон — только чтение).
 *
 * ⚠️ СВОЙ флаг, а не `useEstimateEdit() && frozen === null` строк редакции (§77 C Дизайнера): строки
 * зафиксированной редакции не правятся, а распределение — правится, с пометкой у графика.
 */
export function useDistributionEdit(): boolean {
  const can = useCan();
  const wide = useMedia(WIDE);
  return can.permission('plan.estimate.edit') && wide;
}
