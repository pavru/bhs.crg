import { useCallback, useMemo, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useAccess, hasPermission } from '@/shared/api/access';
import { fetchConstructionDirectory, useConstructionDirectory } from '@/shared/api/constructions';
import { USER_SETTINGS_KEY, useUserSettings, type UserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useSettingsWriteQueue, type SettingsPatch } from '@/shared/hooks/useSettingsWriteQueue';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { SiteFilterContext, type SiteFilterValue } from './siteFilterContext';
import {
  FAVORITES_KEY, FILTER_KEY, LIMIT_TEXT, MAX_FAVORITES, filterOn, filterState, isVisible, parseFavorites,
  serializeFavorites,
} from './rule';

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
  const toast = useSnackbar();

  const sites = useMemo(() => directory.data ?? [], [directory.data]);
  const live = useMemo(() => new Set(sites.map(s => s.id)), [sites]);

  /**
   * Одна запись настроек — общей очередью сеанса (`useSettingsWriteQueue`): сразу в кэш, затем по
   * очереди свежее чтение и запись. Там же — поколение входа и правило «кэш берёт ответ последней записи».
   */
  const enqueue = useSettingsWriteQueue();
  const write = useCallback((
    optimistic: (s: UserSettings) => SettingsPatch,
    fromFresh: (s: UserSettings) => Promise<SettingsPatch> | SettingsPatch,
  ) => enqueue(optimistic, fromFresh).done, [enqueue]);

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

  // Состояние фильтра — отдельно от счётчика потребителей: каждый смонтированный список выбора
  // регистрируется, и пересчитывай его `value` целиком — `favorites` выходило бы новым множеством на
  // каждую регистрацию, сбрасывая мемоизацию у всех списков (ревью PR #1332).
  const on = filterOn(settings.data?.[FILTER_KEY]);
  const state = useMemo(
    () => filterState(parseFavorites(settings.data?.[FAVORITES_KEY]), on, live), [settings.data, on, live]);

  const value = useMemo<SiteFilterValue>(() => {
    // Ошибка — только когда ответа НЕТ: неудачное перечитывание при уже полученных данных фильтр
    // не роняет, он работает по последнему известному.
    const status: SiteFilterValue['status'] =
      !user || (access.data && !allowed) ? 'unavailable'
        : access.data && settings.data && directory.data ? 'ready'
          : access.isError || settings.isError || directory.isError ? 'error' : 'loading';
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
    };
  }, [user, access.data, access.isError, allowed, settings.data, settings.isError, directory.data, directory.isError,
    on, state, sites, write, changeFavorites, toast]);

  return <SiteFilterContext.Provider value={value}>{children}</SiteFilterContext.Provider>;
}
