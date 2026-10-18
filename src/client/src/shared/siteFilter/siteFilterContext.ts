import { createContext, useContext } from 'react';
import type { ObjectSites } from './rule';

/** Стройка справочника — столько, сколько нужно выбору избранного. */
export interface SiteEntry { id: string; name: string }

/**
 * Фильтр «Избранные стройки» (issue #1217, #1218).
 *
 * <ul>
 *   <li>`loading` — не пришли настройки или справочник. Показывать нечего: ни «всё», ни «пусто», ни
 *       чужой отбор. Зеркала в браузере нет нарочно — оно общее на машину, и следующий вошедший
 *       получил бы чужой отбор первым кадром;</li>
 *   <li>`unavailable` — вошедшему не открыт справочник строек: выбирать не из чего, фильтра нет;</li>
 *   <li>`error` — настройки или справочник не пришли вовсе. Фильтр не действует, списки полные, а отбор
 *       «Стройки» говорит, что избранное не загрузилось: «загрузка» без конца держала бы и сами списки (ревью
 *       PR #1331);</li>
 *   <li>`ready` — состояние известно.</li>
 * </ul>
 */
export interface SiteFilterValue {
  status: 'loading' | 'unavailable' | 'error' | 'ready';
  /** Избранное, пересечённое с живым справочником. */
  favorites: ReadonlySet<string>;
  /** Фильтр включён настройкой (ключа нет — включён). */
  on: boolean;
  /** Фильтр работает: известен, включён и избранное непусто. */
  active: boolean;
  /** Живой справочник строек, по названию. */
  sites: readonly SiteEntry[];
  /** Виден ли объект с такими стройками в списке. */
  visible: (sites: ObjectSites) => boolean;
  /** Включить или выключить фильтр; не записалось — сказано тостом. */
  setOn: (on: boolean) => void;
  /** Отметить или снять стройку. Отказ (предел, сеть) — исключением с текстом для человека. */
  setFavorite: (id: string, favorite: boolean) => Promise<void>;
  /** Заменить избранное целиком («Снять все»). */
  setFavorites: (ids: readonly string[]) => Promise<void>;
}

const nothing = async () => {};

/** Вне провайдера фильтра нет: всё видно, писать некуда. */
export const SiteFilterContext = createContext<SiteFilterValue>({
  status: 'unavailable',
  favorites: new Set(),
  on: false,
  active: false,
  sites: [],
  visible: () => true,
  setOn: () => {},
  setFavorite: nothing,
  setFavorites: nothing,
});

/**
 * Состояние фильтра — для выбора избранного, отбора «Стройки», строк на страницах объекта: всего, что
 * о фильтре говорит, но списка не урезает.
 */
export function useSiteFilterState(): SiteFilterValue {
  return useContext(SiteFilterContext);
}

/**
 * Фильтр для СПИСКА, который он урезает. Значение то же, что у `useSiteFilterState`; имя — пометка для
 * сторож-реестра (`siteFilterRegistry.test.ts`): файл, урезающий список, зовёт именно его, и каждый
 * файл, импортирующий хук справочника строек, назван в реестре — «фильтрует» или «не фильтрует,
 * потому что…». Новый экран со стройками иначе молча не фильтровал бы.
 *
 * <p>Счётчика потребителей больше нет (#1432): он зажигал сегмент в шапке, а отбор «Стройки» стоит
 * у самого списка и виден ровно там, где список урезан.</p>
 */
export function useSiteFilter(): SiteFilterValue {
  return useContext(SiteFilterContext);
}
