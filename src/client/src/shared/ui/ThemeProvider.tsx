import { useEffect, useMemo, useSyncExternalStore, type ReactNode } from 'react';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { Ctx, type Theme, type ThemeCtx } from './themeContext';

/**
 * Ключ ЗЕРКАЛА в браузере — не хранилища (issue #953). Выбор темы лежит на сервере и приезжает на
 * другой компьютер; здесь остаётся последнее известное значение, чтобы первый кадр не мигал. Тот
 * же ключ читает `public/theme-init.js` до первой отрисовки — менять его можно только вместе с ним.
 */
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

export function ThemeProvider({ children }: { children: ReactNode }) {
  // Выбор человека хранит сервер, браузер — зеркало (useSyncedPreference). Значения сервер
  // принимает только объявленные (`UserSettingKeys.Theme`), поэтому привести к Theme можно.
  const [stored, setThemeState] = useSyncedPreference('theme', STORAGE_KEY, 'system');
  const theme = stored as Theme;
  const systemTheme = useSyncExternalStore(
    theme === 'system' ? subscribeSystemTheme : NO_SUBSCRIPTION,
    getSystemTheme,
  );
  const resolvedTheme: 'light' | 'dark' = theme === 'system' ? systemTheme : theme;

  // В эффекте остаётся только то, что и есть побочное действие: запись в DOM. Атрибут ставится и
  // при первом рендере — anti-FOUC-скрипт в index.html делает то же самое раньше нас, поэтому
  // мигания не будет, а расхождения не останется. Зеркало в браузере пишет useSyncedPreference.
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', resolvedTheme);
  }, [resolvedTheme]);

  // Значение контекста — мемоизированное: свежий объект-литерал на каждый рендер провайдера
  // перерисовывал бы всех потребителей useTheme даже тогда, когда тема не изменилась.
  const value = useMemo<ThemeCtx>(
    () => ({ theme, setTheme: setThemeState, resolvedTheme }),
    [theme, resolvedTheme],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
