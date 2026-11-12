import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useUserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useSettingsWriteQueue } from '@/shared/hooks/useSettingsWriteQueue';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { useSnackbar } from './Snackbar';
import { PinnedPagesContext, type PinnedPages } from './pinnedPagesContext';
import { PINNED_GROUP_KEY, PINNED_KEY, move, parsePinned, pin, serializePinned, unpin } from './pinnedSections';

/**
 * Закреплённые страницы для оболочки, палитры и профиля (ТЗ AUTH-16.4, work-04) — общей очередью записей
 * сеанса, как «Избранные стройки» (`useSettingsWriteQueue`).
 *
 * Список хранит СЕРВЕР (`pinnedSections`, CORE-25.3): с другой машины он приезжает тот же. Браузер
 * держит зеркало — только для первого кадра, пока настройки не пришли: подпанель не прыгает от
 * появившейся группы. ⚠️ Зеркало подписано хозяином и чужому не показывается: на общей машине иначе
 * вошедший видел бы закрепления прежнего, пока сервер не ответил. Не применяется оно и когда сервер
 * ответил без ключа (`resolvePreference`). Наверх оно не отправляется никогда — запись бывает только по
 * действию человека.
 *
 * Запись — «прочитать свежее и применить правку», а не «отправить свою копию»: закрепление,
 * сделанное тем временем на другой машине, иначе стёрлось бы. Правка ДО прихода настроек кэш не
 * трогает (пустой кэш с одним ключом читался бы ответом сервера) и видна поверх зеркала, пока он не придёт.
 */
const MIRROR = 'crg-pinned';
const GROUP_MIRROR = 'crg-pinned-group';

export function PinnedPagesProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const toast = useSnackbar();
  const { data: server } = useUserSettings(!!user);
  const who = user?.email ?? null;
  const mirror = useMemo(() => readMirror(who), [who]);
  // Правка, сделанная до ответа сервера, — поверх зеркала; пришёл ответ — правда его.
  const [early, setEarly] = useState<{ who: string | null; list: string[] } | null>(null);
  const shown = early?.who === who ? early.list : null;
  const raw = server ? server[PINNED_KEY] ?? null : null;
  const list = useMemo(() => server ? parsePinned(raw) : shown ?? parsePinned(mirror), [server, raw, shown, mirror]);
  useEffect(() => { if (server) writeMirror(who, server[PINNED_KEY] ?? null); }, [server, who]);

  const [group, setGroup] = useSyncedPreference(PINNED_GROUP_KEY, GROUP_MIRROR, 'expanded');

  const enqueue = useSettingsWriteQueue();
  const change = useCallback((edit: (list: string[]) => string[], failure: string) => {
    // Правка — от того, что видит человек: до ответа сервера это зеркало или ранняя правка, а не пустой кэш.
    const before = list;
    const after = edit(before);
    if (after === before) return;
    const { cached, done } = enqueue(
      s => ({ [PINNED_KEY]: serializePinned(edit(parsePinned(s[PINNED_KEY]))) }),
      fresh => ({ [PINNED_KEY]: serializePinned(edit(parsePinned(fresh[PINNED_KEY]))) }));
    if (!cached) setEarly({ who, list: after });
    done.catch(error => toast.apiError(error, failure));
  }, [enqueue, toast, who, list]);

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

/**
 * Зеркало — со своим хозяином: `{ who, value }`. Чужое и негодное — «зеркала нет». Приватный режим и
 * запрет хранилища — не отказ приложения: зеркало просто не запомнится.
 */
function readMirror(who: string | null): string | null {
  if (!who) return null;
  try {
    const saved: unknown = JSON.parse(localStorage.getItem(MIRROR) ?? 'null');
    if (saved && typeof saved === 'object' && (saved as { who?: unknown }).who === who) {
      const value = (saved as { value?: unknown }).value;
      return typeof value === 'string' ? value : null;
    }
    return null;
  } catch { return null; }
}

function writeMirror(who: string | null, value: string | null): void {
  try {
    if (value === null || !who) localStorage.removeItem(MIRROR);
    else localStorage.setItem(MIRROR, JSON.stringify({ who, value }));
  } catch { /* память необязательна */ }
}
