import type { SiteEntry } from './siteFilterContext';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Варианты выбора стройки под фильтром «Избранные стройки» (issue #1219, отчёт Дизайнера §62.4) —
 * чистая функция, без React: одна для списка `Select` и пикера комплектов (там она
 * решает, какие стройки уходят «в сторону», — `useSetTargets`).
 *
 * <ul>
 *   <li>Фильтр не действует — одна группа «Стройки», все. Слово «Избранные» над полным списком было
 *       бы неправдой, а «Стройки» над урезанным — ложью тем более.</li>
 *   <li>Действует — группа «Избранные стройки» и выход «Показать все стройки (ещё N)…» последним
 *       пунктом. Раскрыли — «Избранные стройки» и «Остальные стройки».</li>
 *   <li>Текущее значение — живая, но не избранная стройка (счёт разнесён на неё раньше): своя группа
 *       «Не в избранном», обычным именем. Это НЕ потеря: потерю (стройки нет в справочнике)
 *       подписывает носитель, и смешать два исхода значило бы назвать живую стройку удалённой.</li>
 * </ul>
 *
 * <p>Поиска здесь нет: у списка `Select` его нет вовсе, а пикер комплектов ищет по имени
 * комплекта — и находит пункты «в стороне» всегда, отдельным блоком (`TypePicker`, `aside`).</p>
 */

export const GROUP_ALL = 'Стройки';
export const GROUP_FAVORITES = 'Избранные стройки';
export const GROUP_OTHERS = 'Остальные стройки';
export const GROUP_NOT_FAVORITE = 'Не в избранном';

export interface SiteChoiceGroup<T extends SiteEntry> { label: string; sites: T[] }

export interface SiteChoices<T extends SiteEntry> {
  groups: SiteChoiceGroup<T>[];
  /** Сколько строек спрятано за выходом; 0 — выхода нет. */
  hidden: number;
}

export interface SiteChoiceFilter {
  /** Фильтр работает: известен, включён, избранное непусто. */
  active: boolean;
  favorites: ReadonlySet<string>;
}

/** Текст выхода — последнего пункта списка. */
export function revealText(hidden: number): string {
  return `Показать все стройки (ещё ${hidden})…`;
}

/** Текст выхода в пикере комплектов: прячутся комплекты, а считаются стройки. */
export function revealSetsText(hidden: number): string {
  return `Показать комплекты всех строек (ещё ${hidden} ${ruPlural(hidden, 'стройка', 'стройки', 'строек')})`;
}

export function siteChoices<T extends SiteEntry>(
  sites: readonly T[],
  current: string | null | undefined,
  filter: SiteChoiceFilter,
  revealed: boolean,
): SiteChoices<T> {
  if (!filter.active) return { groups: nonEmpty([{ label: GROUP_ALL, sites: [...sites] }]), hidden: 0 };

  const favorites = sites.filter(s => filter.favorites.has(s.id));
  const others = sites.filter(s => !filter.favorites.has(s.id));
  if (revealed)
    return { groups: nonEmpty([{ label: GROUP_FAVORITES, sites: favorites }, { label: GROUP_OTHERS, sites: others }]), hidden: 0 };

  const kept = others.filter(s => s.id === current);
  return {
    groups: nonEmpty([{ label: GROUP_FAVORITES, sites: favorites }, { label: GROUP_NOT_FAVORITE, sites: kept }]),
    hidden: others.length - kept.length,
  };
}

function nonEmpty<T extends SiteEntry>(groups: SiteChoiceGroup<T>[]): SiteChoiceGroup<T>[] {
  return groups.filter(g => g.sites.length > 0);
}
