import type { PickType } from '@/shared/ui/TypePicker';
import type { Construction } from '@/shared/api/types';
import { useSiteAside } from '@/shared/siteFilter/siteReveal';
import { GROUP_OTHERS } from '@/shared/siteFilter/siteChoices';
import { ruPlural } from '@/shared/utils/pluralize';

export interface TargetSet { id: string; name: string; constructionId: string }

/**
 * Комплекты-цели переноса и копирования (issue #283, #1219): плоский список для пикера, без текущего.
 *
 * <p>Комплекты неизбранных строек уходят «в сторону»: без запроса их нет, внизу — выход «Показать
 * комплекты всех строек (ещё N строек)»; поиск находит их всегда. Стройка ТЕКУЩЕГО комплекта в сторону
 * не уходит, даже не избранная: открыв её прямой ссылкой, человек переносит документ в соседний
 * комплект, и прятать от него эту стройку — то же, что назвать стоящее значение потерей.</p>
 */
export function useSetTargets(constructions: Construction[], currentSetId: string) {
  const isAside = useSiteAside();
  const home = constructions.find(c => c.sections.some(s => s.documentSets.some(ds => ds.id === currentSetId)))?.id;

  const options: PickType[] = [];
  const meta = new Map<string, TargetSet>();
  const asideSites = new Set<string>();
  for (const c of constructions) {
    const aside = c.id !== home && isAside(c.id);
    for (const s of c.sections)
      for (const ds of s.documentSets) {
        if (ds.id === currentSetId) continue;
        options.push({ id: ds.id, name: ds.name, code: s.name, section: c.name, aside });
        meta.set(ds.id, { id: ds.id, name: ds.name, constructionId: c.id });
        if (aside) asideSites.add(c.id);
      }
  }

  const n = asideSites.size;
  const aside = { label: GROUP_OTHERS, reveal: `Показать комплекты всех строек (ещё ${n} ${ruPlural(n, 'стройка', 'стройки', 'строек')})` };
  return { options, meta, aside };
}
