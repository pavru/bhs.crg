import { useMemo } from 'react';
import { useEstimateAttachments } from '@/shared/api/planningConjuncture';
import { mergeAttachments } from './attachmentRows';

/**
 * Приложения сметы обоих видов, новые сверху; `count` — для подписи «Приложения N». Счётчика у сметы нет нарочно:
 * число складывается из двух списков, которые карточка всё равно читает (разбор Архитектора, plan-11).
 */
export function useAttachmentsOf(estimateId: string) {
  const ka = useEstimateAttachments('ka', estimateId);
  const vor = useEstimateAttachments('vor', estimateId);
  const all = useMemo(() => (ka.data && vor.data ? mergeAttachments(ka.data, vor.data) : null), [ka.data, vor.data]);
  return { all, error: ka.error ?? vor.error, count: all?.length ?? null };
}
