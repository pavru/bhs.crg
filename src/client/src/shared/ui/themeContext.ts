import { createContext, useContext } from 'react';
import type { PreferenceSaveState } from '@/shared/hooks/useSyncedPreference';

/**
 * Контекст темы и доступ к нему. Отдельным файлом от провайдера (issue #858): модуль,
 * экспортирующий и компонент, и хук, теряет горячую перезагрузку — правка компонента
 * перезагружает страницу целиком вместо подмены на месте.
 */
export type Theme = 'light' | 'dark' | 'system';

export interface ThemeCtx {
  theme: Theme;
  setTheme: (t: Theme) => void;
  resolvedTheme: 'light' | 'dark';
  /** Чем кончилась последняя отправка выбора на сервер (ревью PR #1000). */
  saveState: PreferenceSaveState;
}

export const Ctx = createContext<ThemeCtx>(
  { theme: 'system', setTheme: () => {}, resolvedTheme: 'light', saveState: 'idle' });

export function useTheme() { return useContext(Ctx); }
