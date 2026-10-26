import { useCan } from '@/shared/api/access';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';

/**
 * Можно ли здесь править сметы: право `plan.estimate.edit` и широкий экран — на телефоне сметы только читают
 * (решение П1). Одно место на загрузку, «Новую редакцию» и «Сделать действующей».
 */
export function useEstimateEdit(): boolean {
  const can = useCan();
  const wide = useMedia(WIDE);
  return can.permission('plan.estimate.edit') && wide;
}
