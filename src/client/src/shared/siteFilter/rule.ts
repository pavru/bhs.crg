/**
 * Правило видимости «Избранных строек» (issue #1217, #1218) — чистые функции, без React.
 *
 * <p>Избранное — фильтр ЭКРАНОВ, а не права: чтение по id не фильтруется нигде, запись в неизбранную
 * стройку разрешена всегда. Здесь решается одно — показывать ли объект в СПИСКЕ.</p>
 *
 * <p>Правило записано и случаями — `siteFilterCases.json`. Его читает этот клиент (vitest) и сервер —
 * ветвь страничного списка накладных (#1222, `WaybillSiteFilter`, тест `WaybillTests.Sites`): две ветви
 * одного правила расходятся молча, а общий файл случаев не даёт им разойтись.</p>
 */

/** Ключи настроек пользователя. Сервер их хранит, но не читает (`FavoriteConstructionValues`). */
export const FAVORITES_KEY = 'favoriteConstructions';
export const FILTER_KEY = 'favoritesFilter';

/** Предел избранного — решение владельца (#1217); сервер отказывает на двадцать первой. */
export const MAX_FAVORITES = 20;

export const LIMIT_TEXT =
  `В избранном может быть не больше ${MAX_FAVORITES} строек. Снимите отметку с другой стройки, чтобы добавить эту.`;

/** Стройки, от которых зависит видимость объекта: одна, несколько или ни одной. */
export type ObjectSites = string | null | undefined | readonly (string | null | undefined)[];

export interface FilterState {
  /** Фильтр работает: включён И избранное непусто после пересечения с живым справочником. */
  active: boolean;
  /** Избранное, уже пересечённое с живым справочником. */
  favorites: ReadonlySet<string>;
  /** Живой справочник строек — id всех существующих. */
  live: ReadonlySet<string>;
}

/** Список из настройки: GUID через запятую. Нет ключа — избранного нет. */
export function parseFavorites(value: string | undefined | null): string[] {
  return value ? value.split(',').filter(Boolean) : [];
}

/** Обратно в настройку. Пустой список не хранится: избранное снимают удалением ключа. */
export function serializeFavorites(ids: readonly string[]): string | null {
  return ids.length ? ids.join(',') : null;
}

/** Ключа нет — фильтр включён: человек, выбравший избранное, ждёт, что оно работает. */
export function filterOn(value: string | undefined | null): boolean {
  return value !== 'off';
}

/**
 * Избранное, пересечённое с живым справочником. Удалённая стройка остаётся в настройках у всех, кто
 * её отметил (чужие настройки при удалении никто не чистит) — и снимается здесь, при каждом чтении.
 */
export function effectiveFavorites(favorites: readonly string[], live: ReadonlySet<string>): Set<string> {
  return new Set(favorites.filter(id => live.has(id)));
}

/**
 * Состояние фильтра. «Избранное пусто → фильтра нет» считается ПОСЛЕ пересечения: удалили последнюю
 * избранную стройку — экраны возвращаются к «всё», а не пустеют.
 */
export function filterState(favorites: readonly string[], on: boolean, live: ReadonlySet<string>): FilterState {
  const effective = effectiveFavorites(favorites, live);
  return { active: on && effective.size > 0, favorites: effective, live };
}

/**
 * Виден ли объект в списке.
 *
 * <ul>
 *   <li>Объект без стройки виден всегда: иначе у снабженца пропала бы рабочая очередь.</li>
 *   <li>Ссылка на удалённую стройку приравнивается к «без стройки» — иначе счета с потерянной
 *       ссылкой (#1184) пропали бы у всех, кто включил фильтр, и никто не узнал бы о потере.</li>
 *   <li>Объект на нескольких стройках виден, если хоть одна избранная.</li>
 * </ul>
 */
export function isVisible(sites: ObjectSites, state: FilterState): boolean {
  if (!state.active) return true;
  const ids = (Array.isArray(sites) ? sites : [sites]) as readonly (string | null | undefined)[];
  const alive = ids.filter((id): id is string => !!id && state.live.has(id));
  if (alive.length === 0) return true;
  return alive.some(id => state.favorites.has(id));
}
