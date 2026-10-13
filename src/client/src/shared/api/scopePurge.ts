import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { purgeOffered, type PurgeOffer } from './recordPurge';

/**
 * Принудительное удаление уровня — стройки, раздела или комплекта (issue #1243): на его содержимое
 * ссылаются только данные выключенного или снятого модуля. Подтверждается общим числом ссылок на
 * всё, что уходит с уровнем.
 */

export type PurgeLevel = 'construction' | 'section' | 'set';

/** Что уходит вместе с уровнем. Документы и записи общих данных сервер считает вместе. */
export interface ScopeLeaving {
  sections: number;
  sets: number;
  objects: number;
  qualityDocuments: number;
  workPlanItems: number;
}

export interface ScopePurgeOffer extends PurgeOffer {
  /** Нет — у человека нет права: состав, как и разбивка, ему не приходит. */
  leaving: ScopeLeaving | null;
}

/** Предложение из отказа удаления уровня — по ПОЛЮ ответа, как у записи (`purgeOffered`). */
export function scopePurgeOffered(e: unknown): ScopePurgeOffer | null {
  const offer = purgeOffered(e);
  if (!offer) return null;
  const leaving = (e as { response?: { data?: { leaving?: Partial<ScopeLeaving> | null } } })?.response?.data?.leaving;
  return {
    ...offer,
    leaving: leaving
      ? {
          sections: leaving.sections ?? 0, sets: leaving.sets ?? 0, objects: leaving.objects ?? 0,
          qualityDocuments: leaving.qualityDocuments ?? 0, workPlanItems: leaving.workPlanItems ?? 0,
        }
      : null,
  };
}

const ADDRESS: Record<PurgeLevel, string> = {
  construction: '/constructions', section: '/sections', set: '/document-sets',
};

export function usePurgeScope(level: PurgeLevel) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, references }: { id: string; references: number }) =>
      apiClient.post<{ name: string; references: number; untraceable: number }>(
        `${ADDRESS[level]}/${id}/purge`, { references }).then(r => r.data),
    // Сбрасывается ВСЁ, и после отказа тоже: с уровнем уходят разделы, комплекты, документы, записи
    // общих данных и планы — перечислять ключи значило бы однажды забыть новый.
    onSettled: () => qc.invalidateQueries(),
  });
}
