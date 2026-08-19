import { useMemo, useState } from 'react';
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

function readStored<K extends string>(storageKey: string, keys: readonly K[]): Partial<Record<K, string>> | null {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    // Совместимость: первая реализация (#778) хранила голый id строкой.
    if (typeof parsed === 'string') return { [keys[0]]: parsed } as Partial<Record<K, string>>;
    return typeof parsed === 'object' && parsed !== null ? parsed as Partial<Record<K, string>> : null;
  } catch { return null; }
}

/**
 * @param storageKey ключ памяти; если один компонент обслуживает несколько маршрутов — включите
 *   в ключ различитель (`types-last:Document`), иначе страницы будут перетирать выбор друг друга.
 * @param keys параметры выбора: они же имена query-параметров, они же поля в памяти.
 */
export function useRememberedSelection<K extends string>(storageKey: string, keys: readonly K[]) {
  const [searchParams, setSearchParams] = useSearchParams();

  // Память читаем один раз при монтировании; дальше значение ведёт URL, а `restored` затухает
  // по мере того, как страница записывает выбор.
  const [restored, setRestored] = useState<Record<K, string>>(() => {
    const fromUrl = {} as Partial<Record<K, string>>;
    for (const k of keys) fromUrl[k] = searchParams.get(k) ?? '';
    return resolveRemembered(keys, fromUrl, readStored(storageKey, keys));
  });

  const values = useMemo(() => {
    const out = {} as Record<K, string>;
    for (const k of keys) out[k] = searchParams.get(k) ?? restored[k] ?? '';
    return out;
    // keys — литеральный массив страницы, в зависимости не берём: его пересоздание на каждом
    // рендере обнуляло бы memo без пользы.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams, restored]);

  const remember = (next: Partial<Record<K, string>>) => {
    const merged = { ...values, ...next } as Record<K, string>;
    try { localStorage.setItem(storageKey, JSON.stringify(merged)); } catch { /* память необязательна */ }
    // Снятый выбор гасит и восстановленное значение: иначе оно всплыло бы обратно, как только
    // параметр уходит из адреса.
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
      return params;
    }, { replace: true });
  };

  return { values, remember };
}
