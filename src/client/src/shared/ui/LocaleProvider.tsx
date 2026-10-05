import { useMemo, type ReactNode } from 'react';
import { LOCALE_KEY, LocaleContext, SYSTEM_LOCALE } from '@/shared/hooks/useLocale';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { setFormatLocale } from '@/shared/format/format';

/**
 * Язык форматирования дат и чисел: хранит сервер, браузер держит зеркало (issue #953, ТЗ
 * CORE-25.3) — как и тема.
 */
export function LocaleProvider({ children }: { children: ReactNode }) {
  const [locale, setLocale, saveState] = useSyncedPreference('locale', LOCALE_KEY, SYSTEM_LOCALE);

  // Форматтеру язык передаётся ПРЯМО В РЕНДЕРЕ, а не эффектом (задача N2, issue #1103): эффект
  // срабатывает после отрисовки потомков, и кадр, в котором язык сменился, нарисовался бы прежним.
  // Вызов повторяемый — то же значение ничего не меняет, — поэтому двойной рендер ему не страшен.
  setFormatLocale(locale);
  const value = useMemo<[string, (v: string) => void, typeof saveState]>(
    () => [locale, setLocale, saveState], [locale, setLocale, saveState]);

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}
