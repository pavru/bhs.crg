import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { USER_SETTINGS_KEY, fetchUserSettings, useUserSettings, type UserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { useSnackbar } from './Snackbar';
import { PinnedPagesContext, type PinnedPages } from './pinnedPagesContext';
import { PINNED_GROUP_KEY, PINNED_KEY, move, parsePinned, pin, serializePinned, unpin } from './pinnedSections';

/**
 * Закреплённые страницы для оболочки, палитры и профиля (ТЗ AUTH-16.4, work-04) — одна очередь записей
 * на сеанс, как у «Избранных строек» (`SiteFilterProvider`).
 *
 * Список хранит СЕРВЕР (`pinnedSections`, CORE-25.3): с другой машины он приезжает тот же. Браузер
 * держит зеркало — только для первого кадра, пока настройки не пришли: подпанель не прыгает от
 * появившейся группы. ⚠️ Зеркало НЕ применяется, когда сервер ответил без ключа: оно осталось от
 * прежнего хозяина машины (`resolvePreference`). И наверх оно не отправляется никогда — запись бывает
 * только по действию человека.
 *
 * Запись — «прочитать свежее и применить правку», а не «отправить свою копию»: закрепление,
 * сделанное тем временем на другой машине, иначе стёрлось бы.
 */
const MIRROR = 'crg-pinned';
const GROUP_MIRROR = 'crg-pinned-group';

/** Хвост очереди записей того, кто уже вышел: его запись не должна лечь на следующего вошедшего. */
class SessionChanged extends Error {}

export function PinnedPagesProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const toast = useSnackbar();
  const { data: server } = useUserSettings(!!user);
  const [mirror] = useState(readMirror);
  const raw = server ? server[PINNED_KEY] ?? null : mirror;
  const list = useMemo(() => parsePinned(raw), [raw]);
  useEffect(() => { if (server) writeMirror(server[PINNED_KEY] ?? null); }, [server]);

  const [group, setGroup] = useSyncedPreference(PINNED_GROUP_KEY, GROUP_MIRROR, 'expanded');

  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const pending = useRef(0);
  const session = useRef(0);
  const who = user?.email ?? null;
  useEffect(() => {
    session.current++;
    queue.current = Promise.resolve();
    pending.current = 0;
  }, [who]);

  const change = useCallback((edit: (list: string[]) => string[], failure: string) => {
    const cached = queryClient.getQueryData<UserSettings>(USER_SETTINGS_KEY) ?? {};
    const before = parsePinned(cached[PINNED_KEY]);
    const after = edit(before);
    if (after === before) return;
    queryClient.setQueryData<UserSettings>(USER_SETTINGS_KEY, withPinned(cached, after));

    const mine = session.current;
    const alive = () => { if (session.current !== mine) throw new SessionChanged(); };
    pending.current++;
    const run = queue.current.then(async () => {
      alive();
      const fresh = await fetchUserSettings();
      alive();
      const next = serializePinned(edit(parsePinned(fresh[PINNED_KEY])));
      return apiClient.put<UserSettings>('/account/settings', { [PINNED_KEY]: next }).then(r => r.data);
    });
    queue.current = run.catch(() => {});
    run.then(
      data => {
        // Кэш берёт ответ только у ПОСЛЕДНЕЙ записи очереди: ответ первой не знает о второй и откатил бы её.
        if (session.current === mine && --pending.current === 0) queryClient.setQueryData(USER_SETTINGS_KEY, data);
      },
      error => {
        if (error instanceof SessionChanged) return;
        // Отказ — кэш возвращается к правде сервера, а не остаётся на оптимистичной догадке.
        if (session.current === mine && --pending.current === 0) {
          void queryClient.invalidateQueries({ queryKey: USER_SETTINGS_KEY });
        }
        toast.apiError(error, failure);
      });
  }, [queryClient, toast]);

  const value = useMemo<PinnedPages>(() => ({
    list,
    has: to => list.includes(to),
    pin: to => change(l => pin(l, to), 'Страница не закреплена'),
    unpin: to => change(l => unpin(l, to), 'Страница не откреплена'),
    move: (from, to) => change(l => {
      // Индексы — по списку, который видел человек; по свежему ищем ту же страницу, а не то же место.
      const page = list[from];
      const target = list[to];
      const a = l.indexOf(page);
      const b = l.indexOf(target);
      return a < 0 || b < 0 ? l : move(l, a, b);
    }, 'Порядок не сохранён'),
    collapsed: group === 'collapsed',
    setCollapsed: c => setGroup(c ? 'collapsed' : 'expanded'),
  }), [list, change, group, setGroup]);

  return <PinnedPagesContext.Provider value={value}>{children}</PinnedPagesContext.Provider>;
}

function withPinned(settings: UserSettings, list: string[]): UserSettings {
  const next = { ...settings };
  const value = serializePinned(list);
  if (value === null) delete next[PINNED_KEY]; else next[PINNED_KEY] = value;
  return next;
}

/** Приватный режим и запрет хранилища — не отказ приложения: зеркало просто не запомнится. */
function readMirror(): string | null {
  try { return localStorage.getItem(MIRROR); } catch { return null; }
}

function writeMirror(value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(MIRROR); else localStorage.setItem(MIRROR, value);
  } catch { /* память необязательна */ }
}
