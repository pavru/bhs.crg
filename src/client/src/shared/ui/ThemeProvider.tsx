import { createContext, useContext, useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react';

export type Theme = 'light' | 'dark' | 'system';

interface ThemeCtx {
  theme: Theme;
  setTheme: (t: Theme) => void;
  resolvedTheme: 'light' | 'dark';
}

const Ctx = createContext<ThemeCtx>({ theme: 'system', setTheme: () => {}, resolvedTheme: 'light' });

const STORAGE_KEY = 'crg-theme';

const SYSTEM_DARK = '(prefers-color-scheme: dark)';

/**
 * Системная тема — ВНЕШНЕЕ хранилище, а не состояние (issue #858).
 *
 * <p>Ею владеет не React, а операционная система: она меняется без нашего участия и меняется у
 * всех вкладок разом. Раньше её держали в `useState`, а синхронизировали двумя эффектами — один
 * переливал в состояние результат `applyTheme`, второй слушал медиа-запрос. Между отрисовкой и
 * эффектом умещался кадр со старой темой, а первый рендер после гидрации мог показать вовсе не то,
 * что уже стоит на `<html>`. `useSyncExternalStore` спрашивает источник в тот же момент, когда
 * React читает всё остальное.</p>
 */
function subscribeSystemTheme(onChange: () => void): () => void {
  const mq = window.matchMedia(SYSTEM_DARK);
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
}

/**
 * Подписка нужна ТОЛЬКО в режиме «как в системе»: при закреплённой светлой или тёмной смена
 * системной настройки ничего не меняет, а перерисовку провайдера вызывала бы — вместе со всеми
 * потребителями useTheme, включая пять редакторов Monaco (поймано ревью PR #863).
 */
const NO_SUBSCRIPTION = () => () => {};

function getSystemTheme(): 'light' | 'dark' {
  return window.matchMedia(SYSTEM_DARK).matches ? 'dark' : 'light';
}

/** Тема, выбранная человеком: сохранённая настройка либо «как в системе». */
function storedTheme(): Theme {
  return (localStorage.getItem(STORAGE_KEY) as Theme | null) ?? 'system';
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(storedTheme);
  const systemTheme = useSyncExternalStore(
    theme === 'system' ? subscribeSystemTheme : NO_SUBSCRIPTION,
    getSystemTheme,
  );
  const resolvedTheme: 'light' | 'dark' = theme === 'system' ? systemTheme : theme;

  // В эффекте остаётся только то, что и есть побочное действие: запись в DOM и в localStorage.
  // Атрибут ставится и при первом рендере — anti-FOUC-скрипт в index.html делает то же самое
  // раньше нас, поэтому мигания не будет, а расхождения не останется.
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', resolvedTheme);
  }, [resolvedTheme]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, theme);
  }, [theme]);

  // Значение контекста — мемоизированное: свежий объект-литерал на каждый рендер провайдера
  // перерисовывал бы всех потребителей useTheme даже тогда, когда тема не изменилась.
  const value = useMemo<ThemeCtx>(
    () => ({ theme, setTheme: setThemeState, resolvedTheme }),
    [theme, resolvedTheme],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useTheme() { return useContext(Ctx); }
