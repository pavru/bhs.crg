import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { useAccess, hasPermission } from '@/shared/api/access';
import { useListConstructions } from '@/shared/api/constructions';
import { USER_SETTINGS_KEY, fetchUserSettings, useUserSettings, type UserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useToast } from '@/shared/ui/Toast';
import { SiteFilterContext, type SiteFilterValue } from './siteFilterContext';
import {
  FAVORITES_KEY, FILTER_KEY, LIMIT_TEXT, MAX_FAVORITES, filterOn, filterState, isVisible, parseFavorites,
  serializeFavorites,
} from './rule';

type Patch = Record<string, string | null>;

/** Присланное поверх прежнего; null снимает ключ — как на сервере. */
function applyPatch(settings: UserSettings | undefined, patch: Patch): UserSettings {
  const next = { ...settings };
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete next[key]; else next[key] = value;
  }
  return next;
}

/**
 * Фильтр «Избранные стройки» (issue #1217, #1218). Стоит в оболочке, рядом с рабочими пространствами.
 *
 * <p><b>Состояние живёт в кэше запроса настроек</b>, а не в `useState`: шапка и список читают одно
 * место и не расходятся ни на кадр. Запись — оптимистичная: кэш правится сразу, сервер — следом.</p>
 *
 * <p><b>Записи идут по одной и по свежему чтению.</b> Кэш настроек живёт пять минут и на каждом
 * компьютере свой; запись списка по нему стёрла бы стройку, отмеченную тем временем на другом
 * (тот же урок, что у пространств, ревью PR #1330). Поэтому операция «отметить / снять» применяется
 * к списку, перечитанному с сервера, а очередь не даёт двум быстрым щелчкам прочитать одно и то же.</p>
 *
 * <p><b>Справочник — основной `/constructions`</b>, по праву `core.constructions.read`: его выдают
 * каждой рабочей роли. Без права выбирать не из чего — фильтра нет (`unavailable`).</p>
 */
export function SiteFilterProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const { data: access } = useAccess(!!user);
  const allowed = !!access && hasPermission(access, 'core.constructions.read');
  const { data: settings } = useUserSettings(!!user);
  const { data: constructions } = useListConstructions(allowed);
  const queryClient = useQueryClient();
  const toast = useToast();
  const [consumers, setConsumers] = useState(0);

  const sites = useMemo(
    () => (constructions ?? []).map(c => ({ id: c.id, name: c.name })).sort((a, b) => a.name.localeCompare(b.name, 'ru')),
    [constructions]);
  const live = useMemo(() => new Set(sites.map(s => s.id)), [sites]);

  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const pending = useRef(0);

  /**
   * Одна запись настроек: сразу — в кэш (`optimistic` от закэшированного), затем по очереди —
   * свежее чтение и запись (`fromFresh` от прочитанного). Кэш берёт ответ сервера только у ПОСЛЕДНЕЙ
   * записи очереди: ответ первой не знает о второй и откатил бы её на глазах.
   */
  const write = useCallback((optimistic: (s: UserSettings) => Patch, fromFresh: (s: UserSettings) => Patch) => {
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY) ?? {};
    queryClient.setQueryData<UserSettings>(USER_SETTINGS_KEY, applyPatch(cached, optimistic(cached)));
    pending.current++;
    const run = queue.current.then(async () => {
      const fresh = await fetchUserSettings();
      return apiClient.put<UserSettings>('/account/settings', fromFresh(fresh)).then(r => r.data);
    });
    queue.current = run.catch(() => {});
    return run.then(
      data => { if (--pending.current === 0) queryClient.setQueryData(USER_SETTINGS_KEY, data); },
      error => {
        // Отказ — кэш возвращается к правде сервера, а не остаётся на оптимистичной догадке.
        if (--pending.current === 0) void queryClient.invalidateQueries({ queryKey: USER_SETTINGS_KEY });
        throw error;
      });
  }, [queryClient]);

  /** Операция над избранным: мёртвые id снимаются при каждой записи, предел проверяется до неё. */
  const changeFavorites = useCallback((change: (ids: string[]) => string[]) => {
    const next = (s: UserSettings) => change(parseFavorites(s[FAVORITES_KEY]).filter(id => live.has(id)));
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY) ?? {};
    if (next(cached).length > MAX_FAVORITES) return Promise.reject(new Error(LIMIT_TEXT));
    return write(
      s => ({ [FAVORITES_KEY]: serializeFavorites(next(s)) }),
      s => {
        const ids = next(s);
        // Предел ещё раз — по свежему списку: с другого компьютера тем временем могли добавить.
        if (ids.length > MAX_FAVORITES) throw new Error(LIMIT_TEXT);
        return { [FAVORITES_KEY]: serializeFavorites(ids) };
      });
  }, [queryClient, write, live]);

  const register = useCallback(() => {
    setConsumers(n => n + 1);
    return () => setConsumers(n => n - 1);
  }, []);

  const value = useMemo<SiteFilterValue>(() => {
    const status: SiteFilterValue['status'] = !user || (access && !allowed) ? 'unavailable'
      : !access || !settings || !constructions ? 'loading' : 'ready';
    const on = filterOn(settings?.[FILTER_KEY]);
    const state = filterState(parseFavorites(settings?.[FAVORITES_KEY]), on, live);
    const active = status === 'ready' && state.active;
    return {
      status,
      favorites: state.favorites,
      on,
      active,
      sites,
      visible: objectSites => isVisible(objectSites, { ...state, active }),
      setOn: next => {
        // Ключа нет — включён: включение снимает ключ, а не пишет «on».
        const patch = { [FILTER_KEY]: next ? null : 'off' };
        write(() => patch, () => patch).catch(e =>
          toast.apiError(e, next ? 'Фильтр избранного не включён' : 'Фильтр избранного не выключен'));
      },
      setFavorite: (id, favorite) => changeFavorites(ids =>
        favorite ? [...ids.filter(x => x !== id), id] : ids.filter(x => x !== id)),
      setFavorites: wanted => changeFavorites(() => [...wanted]),
      consumers,
      register,
    };
  }, [user, access, allowed, settings, constructions, live, sites, write, changeFavorites, consumers, register, toast]);

  return <SiteFilterContext.Provider value={value}>{children}</SiteFilterContext.Provider>;
}
