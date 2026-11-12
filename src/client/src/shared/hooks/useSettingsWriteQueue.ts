import { useCallback, useEffect, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { USER_SETTINGS_KEY, fetchUserSettings, type UserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';

/** Частичная правка настроек: null снимает ключ — как на сервере. */
export type SettingsPatch = Record<string, string | null>;

/** Присланное поверх прежнего; null снимает ключ — как на сервере. */
export function applySettingsPatch(settings: UserSettings | undefined, patch: SettingsPatch): UserSettings {
  const next = { ...settings };
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete next[key]; else next[key] = value;
  }
  return next;
}

/** Хвост очереди записей того, кто уже вышел: его запись не должна лечь на следующего вошедшего. */
export class SessionChanged extends Error {}

/**
 * Очередь записей серверных настроек одного сеанса — у «Избранных строек» (#1217) и закреплённых
 * страниц (work-04). Одна машинерия на обоих: поправленная гонка в одной копии в другую не попала бы.
 *
 * <p><b>Записи идут по одной и по свежему чтению.</b> Кэш на каждом компьютере свой: запись по нему
 * стёрла бы правку, сделанную тем временем на другом (урок пространств, ревью PR #1330). Очередь не даёт
 * двум быстрым щелчкам прочитать одно и то же.</p>
 *
 * <p><b>Кэш правится сразу (`optimistic`) — только когда настройки уже пришли.</b> Пустой кэш, засеянный
 * одной правкой, выглядел бы ответом сервера без остальных ключей: `resolvePreference` сбросил бы по нему
 * чужие предпочтения к умолчанию до прихода настоящего ответа. До ответа показать правку — дело
 * вызывающего (`cached` в ответе — пришли ли настройки).</p>
 *
 * <p><b>Кэш берёт ответ сервера только у ПОСЛЕДНЕЙ записи очереди</b>: ответ первой не знает о второй и
 * откатил бы её на глазах. Отказ — кэш перечитывается: правда сервера, а не оптимистичная догадка.</p>
 *
 * <p><b>Поколение входа</b>: сменился человек — отложенные записи прежнего отбрасываются (ревью PR #1331).
 * Кэш при выходе чистит `AuthProvider`, но очередь живёт здесь, и её хвост выполнился бы уже с токеном
 * следующего вошедшего.</p>
 */
export function useSettingsWriteQueue() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const pending = useRef(0);
  const session = useRef(0);
  const who = user?.email ?? null;
  useEffect(() => {
    session.current++;
    queue.current = Promise.resolve();
    pending.current = 0;
  }, [who]);

  return useCallback((
    optimistic: (s: UserSettings) => SettingsPatch,
    fromFresh: (s: UserSettings) => Promise<SettingsPatch> | SettingsPatch,
  ): { cached: boolean; done: Promise<void> } => {
    const mine = session.current;
    const alive = () => { if (session.current !== mine) throw new SessionChanged(); };
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY);
    if (cached) queryClient.setQueryData<UserSettings>(USER_SETTINGS_KEY, applySettingsPatch(cached, optimistic(cached)));
    pending.current++;
    const run = queue.current.then(async () => {
      alive();
      const patch = await fromFresh(await fetchUserSettings());
      alive();
      return apiClient.put<UserSettings>('/account/settings', patch).then(r => r.data);
    });
    queue.current = run.catch(() => {});
    const done = run.then(
      data => {
        if (session.current === mine && --pending.current === 0) queryClient.setQueryData(USER_SETTINGS_KEY, data);
      },
      error => {
        if (error instanceof SessionChanged) return;
        if (session.current === mine && --pending.current === 0) {
          void queryClient.invalidateQueries({ queryKey: USER_SETTINGS_KEY });
        }
        throw error;
      });
    return { cached: !!cached, done };
  }, [queryClient]);
}
