import { useMemo } from 'react';
import type { PickType } from '@/shared/ui/TypePicker';
import type { Construction } from '@/shared/api/types';
import { useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { GROUP_OTHERS, revealSetsText } from '@/shared/siteFilter/siteChoices';

export interface TargetSet { id: string; name: string; constructionId: string }

/**
 * Комплекты-цели переноса и копирования (issue #283, #1219): плоский список для пикера, без текущего.
 *
 * <p>Какие стройки видны, решает то же правило, что у списков выбора стройки (`siteChoices`): стройки
 * вне его групп уходят «в сторону» — без запроса их комплектов нет, внизу выход «Показать комплекты
 * всех строек (ещё N строек)»; поиск находит их всегда. Стройка ТЕКУЩЕГО комплекта передана правилу
 * как стоящее значение и в сторону не уходит, даже не избранная: открыв её прямой ссылкой, человек
 * переносит документ в соседний комплект, и прятать от него эту стройку — то же, что назвать стоящее
 * значение потерей. Считаются только стройки, где есть куда переносить.</p>
 */
export function useSetTargets(constructions: Construction[], currentSetId: string) {
  const home = useMemo(
    () => constructions.find(c => c.sections.some(s => s.documentSets.some(ds => ds.id === currentSetId)))?.id,
    [constructions, currentSetId]);
  const withTargets = useMemo(
    () => constructions.filter(c => c.sections.some(s => s.documentSets.some(ds => ds.id !== currentSetId))),
    [constructions, currentSetId]);
  // Зависимости — группы и число, а не объект целиком: объект хук собирает заново на каждой отрисовке.
  const { groups, hidden } = useSiteChoices(withTargets, home);

  return useMemo(() => {
    const shown = new Set(groups.flatMap(g => g.sites.map(s => s.id)));
    const options: PickType[] = [];
    const meta = new Map<string, TargetSet>();
    for (const c of withTargets)
      for (const s of c.sections)
        for (const ds of s.documentSets) {
          if (ds.id === currentSetId) continue;
          options.push({ id: ds.id, name: ds.name, code: s.name, section: c.name, aside: !shown.has(c.id) });
          meta.set(ds.id, { id: ds.id, name: ds.name, constructionId: c.id });
        }
    return { options, meta, aside: { label: GROUP_OTHERS, reveal: revealSetsText(hidden) } };
  }, [withTargets, currentSetId, groups, hidden]);
}
