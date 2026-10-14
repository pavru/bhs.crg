import { createContext, useContext, useLayoutEffect } from 'react';
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
 *   <li>`error` — настройки или справочник не пришли вовсе. Фильтр не действует, списки полные, а шапка
 *       говорит, что избранное не загрузилось: «загрузка» без конца держала бы и сами списки (ревью
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
  /** Сколько на экране смонтировано списков, которые фильтр урезает. */
  consumers: number;
  /** Объявить себя потребителем; возвращает снятие. Зовёт `useSiteFilter`, а не экраны. */
  register: () => () => void;
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
  consumers: 0,
  register: () => () => {},
});

/**
 * Состояние фильтра БЕЗ регистрации — для шапки, выбора избранного, строк на страницах объекта:
 * всего, что о фильтре говорит, но списка не урезает.
 */
export function useSiteFilterState(): SiteFilterValue {
  return useContext(SiteFilterContext);
}

/**
 * Фильтр для СПИСКА, который он урезает. Вызов регистрирует экран как потребителя — и только тогда
 * сегмент в шапке «горит». Таблицы маршрутов нет нарочно: её забыли бы обновить, и шапка говорила бы
 * «включено» над списком, который не урезан, или «не действуют» над урезанным.
 *
 * ⚠️ Каждый файл, импортирующий хук справочника строек, назван в сторож-реестре
 * (`siteFilterRegistry.test.ts`): «фильтрует» или «не фильтрует, потому что…». Новый экран со
 * стройками иначе молча не фильтровал бы.
 */
export function useSiteFilter(): SiteFilterValue {
  const value = useContext(SiteFilterContext);
  const { register } = value;
  // До отрисовки кадра: иначе шапка успела бы показать «не действуют» над урезанным списком.
  useLayoutEffect(() => register(), [register]);
  return value;
}
