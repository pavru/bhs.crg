import { Star } from 'lucide-react';
import type { PanelGroup } from '@/shared/ui/railSections';
import { useSiteFilterState } from './siteFilterContext';

export const FAVORITE_SITES_GROUP = 'Избранные стройки';

/**
 * Закреплённые стройки в подпанели раздела «Стройки» (issue #1432) — быстрый переход к карточке. Шапка
 * больше избранного не показывает, а в подпанели оно стоит там, где стройки и ищут.
 *
 * Группы нет, пока избранное не известно или пусто: пустая группа звала бы отмечать, а звезда стоит
 * на карточке рядом. Порядок — справочника, по названию: перечень короткий (не больше 20), и место
 * стройки в нём не меняется от того, когда её отметили.
 */
export function useFavoriteSitesGroup(): PanelGroup | null {
  const filter = useSiteFilterState();
  if (filter.status !== 'ready' || filter.favorites.size === 0) return null;
  return {
    label: FAVORITE_SITES_GROUP,
    items: filter.sites.filter(s => filter.favorites.has(s.id))
      .map(s => ({ to: `/document-sets/${s.id}`, label: s.name, icon: Star, rail: 'constructions' })),
  };
}
