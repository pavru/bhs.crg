import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';

export type Theme = 'light' | 'dark' | 'system';

interface ThemeCtx {
  theme: Theme;
  setTheme: (t: Theme) => void;
  resolvedTheme: 'light' | 'dark';
}

const Ctx = createContext<ThemeCtx>({ theme: 'system', setTheme: () => {}, resolvedTheme: 'light' });

const STORAGE_KEY = 'crg-theme';

function getSystemTheme(): 'light' | 'dark' {
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function applyTheme(theme: Theme): 'light' | 'dark' {
  const resolved = theme === 'system' ? getSystemTheme() : theme;
  document.documentElement.setAttribute('data-theme', resolved);
  return resolved;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(
    () => (localStorage.getItem(STORAGE_KEY) as Theme | null) ?? 'system',
  );
  const [resolvedTheme, setResolved] = useState<'light' | 'dark'>(() => applyTheme(
    (localStorage.getItem(STORAGE_KEY) as Theme | null) ?? 'system',
  ));

  useEffect(() => {
    const resolved = applyTheme(theme);
    setResolved(resolved);
    localStorage.setItem(STORAGE_KEY, theme);
  }, [theme]);

  useEffect(() => {
    if (theme !== 'system') return;
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const handler = () => {
      const resolved = applyTheme('system');
      setResolved(resolved);
    };
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, [theme]);

  function setTheme(t: Theme) { setThemeState(t); }

  return <Ctx.Provider value={{ theme, setTheme, resolvedTheme }}>{children}</Ctx.Provider>;
}

export function useTheme() { return useContext(Ctx); }
