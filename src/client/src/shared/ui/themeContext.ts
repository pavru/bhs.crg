import { createContext, useContext } from 'react';

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
}

export const Ctx = createContext<ThemeCtx>({ theme: 'system', setTheme: () => {}, resolvedTheme: 'light' });

export function useTheme() { return useContext(Ctx); }
