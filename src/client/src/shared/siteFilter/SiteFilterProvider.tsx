import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { useAccess, hasPermission } from '@/shared/api/access';
import { fetchConstructionDirectory, useConstructionDirectory } from '@/shared/api/constructions';
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

/** Хвост очереди записей того, кто уже вышел: его запись не должна лечь на следующего вошедшего. */
class SessionChanged extends Error {}

/**
 * Фильтр «Избранные стройки» (issue #1217, #1218). Стоит в оболочке, рядом с рабочими пространствами.
 *
 * <p><b>Состояние живёт в кэше запроса настроек</b>, а не в `useState`: шапка и список читают одно
 * место и не расходятся ни на кадр. Запись — оптимистичная: кэш правится сразу, сервер — следом.</p>
 *
 * <p><b>Записи идут по одной и по свежему чтению</b> — и настроек, и справочника строек. Кэш на
 * каждом компьютере свой: запись по нему стёрла бы стройку, отмеченную тем временем на другом (урок
 * пространств, ревью PR #1330), а снятие «удалённых» по своему справочнику — стройку, которой этот
 * компьютер ещё не видел (ревью PR #1331). Очередь не даёт двум быстрым щелчкам прочитать одно и то же.</p>
 *
 * <p><b>Справочник — лёгкий `/constructions/directory`</b>, по праву `core.constructions.read`: его
 * выдают каждой рабочей роли. Без права выбирать не из чего — фильтра нет (`unavailable`). Не
 * пришли настройки или справочник — `error`: фильтр не действует и говорит об этом, а не висит
 * «загрузкой», за которой ждали бы и списки.</p>
 */
export function SiteFilterProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const access = useAccess(!!user);
  const allowed = !!access.data && hasPermission(access.data, 'core.constructions.read');
  const settings = useUserSettings(!!user);
  const directory = useConstructionDirectory(allowed);
  const queryClient = useQueryClient();
  const toast = useToast();
  const [consumers, setConsumers] = useState(0);

  const sites = useMemo(() => directory.data ?? [], [directory.data]);
  const live = useMemo(() => new Set(sites.map(s => s.id)), [sites]);

  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const pending = useRef(0);
  // Поколение входа: сменился человек — отложенные записи прежнего отбрасываются (ревью PR #1331).
  // Кэш при выходе чистит `AuthProvider`, но очередь живёт здесь, и её хвост выполнился бы уже с
  // токеном следующего вошедшего, наложив чужое избранное на его настройки.
  const session = useRef(0);
  const who = user?.email ?? null;
  useEffect(() => {
    session.current++;
    queue.current = Promise.resolve();
    pending.current = 0;
  }, [who]);

  /**
   * Одна запись настроек: сразу — в кэш (`optimistic` от закэшированного), затем по очереди —
   * свежее чтение и запись (`fromFresh` от прочитанного). Кэш берёт ответ сервера только у ПОСЛЕДНЕЙ
   * записи очереди: ответ первой не знает о второй и откатил бы её на глазах.
   */
  const write = useCallback((
    optimistic: (s: UserSettings) => Patch,
    fromFresh: (s: UserSettings) => Promise<Patch> | Patch,
  ) => {
    const mine = session.current;
    const alive = () => { if (session.current !== mine) throw new SessionChanged(); };
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY) ?? {};
    queryClient.setQueryData<UserSettings>(USER_SETTINGS_KEY, applyPatch(cached, optimistic(cached)));
    pending.current++;
    const run = queue.current.then(async () => {
      alive();
      const patch = await fromFresh(await fetchUserSettings());
      alive();
      return apiClient.put<UserSettings>('/account/settings', patch).then(r => r.data);
    });
    queue.current = run.catch(() => {});
    return run.then(
      data => {
        if (session.current === mine && --pending.current === 0) queryClient.setQueryData(USER_SETTINGS_KEY, data);
      },
      error => {
        if (error instanceof SessionChanged) return;
        // Отказ — кэш возвращается к правде сервера, а не остаётся на оптимистичной догадке.
        if (session.current === mine && --pending.current === 0) {
          void queryClient.invalidateQueries({ queryKey: USER_SETTINGS_KEY });
        }
        throw error;
      });
  }, [queryClient]);

  /**
   * Операция над избранным. Оптимистично — по своему кэшу; на сервер — по свежим настройкам и
   * СВЕЖЕМУ справочнику: удалённые стройки снимаются только те, которых сервер не знает сейчас.
   */
  const changeFavorites = useCallback((change: (ids: string[]) => string[]) => {
    const cachedIds = (s: UserSettings) => parseFavorites(s[FAVORITES_KEY]).filter(id => live.has(id));
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY) ?? {};
    if (change(cachedIds(cached)).length > MAX_FAVORITES) return Promise.reject(new Error(LIMIT_TEXT));
    return write(
      s => ({ [FAVORITES_KEY]: serializeFavorites(change(cachedIds(s))) }),
      async s => {
        const known = new Set((await fetchConstructionDirectory()).map(c => c.id));
        const ids = change(parseFavorites(s[FAVORITES_KEY]).filter(id => known.has(id)));
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
    // Ошибка — только когда ответа НЕТ: неудачное перечитывание при уже полученных данных фильтр
    // не роняет, он работает по последнему известному.
    const status: SiteFilterValue['status'] =
      !user || (access.data && !allowed) ? 'unavailable'
        : access.data && settings.data && directory.data ? 'ready'
          : access.isError || settings.isError || directory.isError ? 'error' : 'loading';
    const on = filterOn(settings.data?.[FILTER_KEY]);
    const state = filterState(parseFavorites(settings.data?.[FAVORITES_KEY]), on, live);
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
  }, [user, access.data, access.isError, allowed, settings.data, settings.isError, directory.data, directory.isError,
    live, sites, write, changeFavorites, consumers, register, toast]);

  return <SiteFilterContext.Provider value={value}>{children}</SiteFilterContext.Provider>;
}
