import { useMemo, type ReactNode } from 'react';
import { LOCALE_KEY, LocaleContext, SYSTEM_LOCALE } from '@/shared/hooks/useLocale';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';

/**
 * Язык форматирования дат и чисел: хранит сервер, браузер держит зеркало (issue #953, ТЗ
 * CORE-25.3) — как и тема.
 */
export function LocaleProvider({ children }: { children: ReactNode }) {
  const [locale, setLocale] = useSyncedPreference('locale', LOCALE_KEY, SYSTEM_LOCALE);
  const value = useMemo<[string, (v: string) => void]>(() => [locale, setLocale], [locale, setLocale]);

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}
