import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { useQueryClient } from '@tanstack/react-query';
import { USER_SETTINGS_KEY, fetchUserSettings, useSaveUserSettings, useUserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { useSnackbar } from './Snackbar';
import { WorkspaceContext, type WorkspaceContextValue } from './workspaceContext';
import { readTab, tabKey as tabKeyOf, writeTab } from './workspaceTab';
import {
  DEFAULT_KEY, LAST_KEY, OWN_KEY, inWorkspace, moduleInWorkspace, parseOwn, sectionInWorkspace, resolveWorkspace, startChoice, switcherShown,
  workspaceList,
} from './workspaces';

/**
 * Рабочее пространство открытого экрана (ТЗ AUTH-16.1; issue #1001). Выбор, свои пространства и
 * умолчание хранит СЕРВЕР настройками пользователя — переживают перезаход и смену компьютера.
 * В запросы к API пространство не уходит (AUTH-16.2).
 */
export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const { data: access = NO_ACCESS } = useAccess();
  const { data: settings } = useUserSettings(!!user);
  // Сами функции записи, а не объект мутации: он новый на каждой отрисовке и тянул бы за собой
  // значение контекста — а с ним всех, кто его читает.
  const { mutate, mutateAsync } = useSaveUserSettings();
  const toast = useSnackbar();
  const queryClient = useQueryClient();

  // Вкладка помнит выбор до конца сессии (`workspaceTab.ts`); новая вкладка, новый вход — умолчание
  // (AUTH-16.5). Ключ с почтой: вошёл другой человек — чужой выбор не его.
  const tabKey = user ? tabKeyOf(user.email) : null;
  const [picked, setPicked] = useState<string | null>(() => readTab(tabKey));

  const own = useMemo(() => parseOwn(settings?.[OWN_KEY]), [settings]);
  const resolved = useMemo(
    () => resolveWorkspace(access, own, startChoice(settings, picked)),
    [access, own, settings, picked]);

  // Откат на «все доступные» — разовым сообщением, при входе (AUTH-16.3): постоянная плашка здесь
  // не нужна. «Разовое» держит вкладка: перезагрузка не повторяет его, новый вход — повторит.
  const notice = resolved.notice;
  useEffect(() => {
    if (!notice || !tabKey) return;
    const shownKey = `${tabKey}:notice`;
    if (readTab(shownKey) === notice) return;
    writeTab(shownKey, notice);
    // Дольше обычных 4 с — фраза длинная; но не 10: уведомления идут по одному, и сохранение в первые
    // секунды после входа ждало бы за этим.
    toast.info(notice, { duration: 6000 });
  }, [notice, tabKey, toast]);

  const value = useMemo<WorkspaceContextValue>(() => {
    const current = resolved.workspace;
    const inside: WorkspaceContextValue['inside'] = item => inWorkspace(item, current, access);
    return {
      shown: switcherShown(access),
      current,
      list: workspaceList(access, own),
      own,
      defaultChoice: settings?.[DEFAULT_KEY] ?? null,
      choose: key => {
        setPicked(key);
        writeTab(tabKey, key);
        // Последнее выбранное — на сервер: с другого компьютера человек вернётся сюда же. Не
        // записалось — сказать: иначе завтра откроется не то, и выглядело бы это сбоем пространств.
        mutate({ [LAST_KEY]: key }, {
          onError: e => toast.apiError(e, 'Выбор пространства не запомнен — после входа откроется прежнее'),
        });
      },
      updateOwn: async (change, also) => {
        // Свежие настройки — мимо кэша. Окно между чтением и записью — доли секунды и один человек;
        // кэш жил бы пять минут и на каждом компьютере свой.
        const fresh = await queryClient.fetchQuery({ queryKey: USER_SETTINGS_KEY, queryFn: fetchUserSettings, staleTime: 0 });
        const next = change(parseOwn(fresh[OWN_KEY]));
        if (typeof next === 'string') throw new Error(next);
        await mutateAsync({ [OWN_KEY]: next.length ? JSON.stringify(next) : null, ...also?.(fresh) });
      },
      setDefault: async key => {
        await mutateAsync({ [DEFAULT_KEY]: key });
      },
      shows: module => moduleInWorkspace(module, current, access),
      showsSection: section => sectionInWorkspace(section, current, access),
      inside,
    };
  }, [resolved, access, own, settings, tabKey, mutate, mutateAsync, toast, queryClient]);

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}
