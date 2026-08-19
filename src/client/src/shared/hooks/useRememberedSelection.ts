import { startTransition, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';

/**
 * Конвенция list-detail (issue #787, после #778/#780): выбор живёт в URL, последний открытый —
 * в localStorage. Страница открывается там, где работу оставили, а конкретный выбор можно дать
 * ссылкой.
 *
 * Правила, выстраданные первыми двумя реализациями:
 * - в URL пишем `replace`, иначе стрелки браузера ходят по каждому клику в списке;
 * - при входе: URL → память → пусто. Дальше решает URL, память лишь зеркалит выбор;
 * - восстановленное значение, которое больше не разрешается (объект удалён, не проходит фильтр
 *   страницы), страница обязана пропускать молча — хелпер за неё этого не знает;
 * - память чистим тем же вызовом, каким снимается выбор: `remember({ type: '' })`.
 */

/** Разрешение источника при входе: URL перекрывает память, память — пустое значение. */
export function resolveRemembered<K extends string>(
  keys: readonly K[],
  fromUrl: Partial<Record<K, string>>,
  stored: Partial<Record<K, string>> | null,
): Record<K, string> {
  // Хоть один параметр в адресе — значит пришли по ссылке: адрес главнее памяти целиком,
  // иначе половина выбора взялась бы из ссылки, а половина из прошлой сессии.
  const urlHasAny = keys.some(k => (fromUrl[k] ?? '') !== '');
  const source: Partial<Record<K, string>> = urlHasAny ? fromUrl : (stored ?? {});
  const out = {} as Record<K, string>;
  for (const k of keys) out[k] = source[k] ?? '';
  return out;
}

/** Разбор сохранённого значения; вынесен из хука, чтобы форматы проверялись тестами. */
export function parseStored<K extends string>(raw: string | null, keys: readonly K[]): Partial<Record<K, string>> | null {
  try {
    if (!raw) return null;
    // Совместимость: первая реализация (#778) хранила голый id строкой — не JSON, и разбирать её
    // как JSON бесполезно (`JSON.parse` на «3fa85f64-…» бросает, а не возвращает строку).
    if (!raw.startsWith('{')) return { [keys[0]]: raw } as Partial<Record<K, string>>;
    const parsed: unknown = JSON.parse(raw);
    return typeof parsed === 'object' && parsed !== null ? parsed as Partial<Record<K, string>> : null;
  } catch { return null; }
}

function readStored<K extends string>(storageKey: string, keys: readonly K[]): Partial<Record<K, string>> | null {
  try { return parseStored(localStorage.getItem(storageKey), keys); } catch { return null; }
}

/**
 * @param storageKey ключ памяти; если один компонент обслуживает несколько маршрутов — включите
 *   в ключ различитель (`types-last:Document`), иначе страницы будут перетирать выбор друг друга.
 * @param keys параметры выбора: они же имена query-параметров, они же поля в памяти.
 */
export function useRememberedSelection<K extends string>(storageKey: string, keys: readonly K[]) {
  const [searchParams, setSearchParams] = useSearchParams();

  // Память читаем один раз на ключ; дальше значение ведёт URL, а `restored` затухает по мере
  // того, как страница записывает выбор. Ключ входит в состояние: один компонент может
  // обслуживать несколько уровней (наборы данных — систему, стройку, раздел, комплект), а роутер
  // переиспользует экземпляр при смене параметров маршрута — с прочитанной один раз памятью
  // выбор с прошлого уровня всплыл бы на новом.
  const readForKey = (): Record<K, string> => {
    const fromUrl = {} as Partial<Record<K, string>>;
    for (const k of keys) fromUrl[k] = searchParams.get(k) ?? '';
    return resolveRemembered(keys, fromUrl, readStored(storageKey, keys));
  };
  const [restoredState, setRestoredState] = useState<{ key: string; values: Record<K, string> }>(
    () => ({ key: storageKey, values: readForKey() }));
  if (restoredState.key !== storageKey) setRestoredState({ key: storageKey, values: readForKey() });
  const restored = restoredState.values;
  const setRestored = (update: (prev: Record<K, string>) => Record<K, string>) =>
    setRestoredState(prev => ({ key: prev.key, values: update(prev.values) }));

  const values = useMemo(() => {
    const out = {} as Record<K, string>;
    // `get` отдаёт '' для параметра, который в адресе есть, но пуст, — за источник его не берём:
    // иначе `?mode=` погасил бы режим и оставил тип из памяти, то есть собрал бы пару, которой
    // пользователь не выбирал (ровно то, от чего страхует resolveRemembered).
    for (const k of keys) out[k] = searchParams.get(k) || restored[k] || '';
    return out;
    // keys — литеральный массив страницы, в зависимости не берём: его пересоздание на каждом
    // рендере обнуляло бы memo без пользы.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams, restored]);

  /**
   * @param mutate необязательная правка соседних параметров адреса. Делать её отдельным
   *   `setSearchParams` нельзя: колбэк получает значение времени рендера, поэтому два вызова в
   *   одном такте не накапливаются — второй перетирает первый (документировано в react-router).
   */
  const remember = (next: Partial<Record<K, string>>, mutate?: (params: URLSearchParams) => void) => {
    const merged = { ...values, ...next } as Record<K, string>;
    try { localStorage.setItem(storageKey, JSON.stringify(merged)); } catch { /* память необязательна */ }
    // Оба обновления — одним переходом. Порознь нельзя: адрес пишется через startTransition, а
    // срочный сброс восстановленного успел бы отрисовать кадр, где часть выбора уже снята, а часть
    // ещё берётся из старого адреса — деталь смонтировалась бы на чужом объекте и тут же
    // перемонтировалась на нужный.
    startTransition(() => {
      setRestored(prev => {
        const cleared = { ...prev };
        let changed = false;
        for (const k of keys) if ((next[k] ?? '') === '' && k in next && cleared[k] !== '') { cleared[k] = ''; changed = true; }
        return changed ? cleared : prev;
      });
      setSearchParams(prev => {
        const params = new URLSearchParams(prev);
        for (const k of keys) {
          const v = merged[k];
          if (v) params.set(k, v); else params.delete(k);
        }
        mutate?.(params);
        return params;
      }, { replace: true });
    });
  };

  return { values, remember };
}
