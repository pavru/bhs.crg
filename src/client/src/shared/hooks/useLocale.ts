import { useState } from 'react';

export const LOCALE_KEY = 'crg.locale';
export const SYSTEM_LOCALE = 'system';

export interface LocaleOption {
  value: string;
  label: string;
  nativeLabel: string;
}

export const LOCALE_OPTIONS: LocaleOption[] = [
  { value: 'system', label: 'Системная',        nativeLabel: 'System' },
  { value: 'ru-RU',  label: 'Русский (Россия)',  nativeLabel: 'Русский' },
  { value: 'en-US',  label: 'English (US)',      nativeLabel: 'English (US)' },
  { value: 'en-GB',  label: 'English (UK)',      nativeLabel: 'English (UK)' },
  { value: 'de-DE',  label: 'Deutsch',           nativeLabel: 'Deutsch' },
];

export function useLocale(): [string, (locale: string) => void] {
  const [locale, setLocaleState] = useState(
    () => localStorage.getItem(LOCALE_KEY) ?? SYSTEM_LOCALE,
  );

  function setLocale(l: string) {
    localStorage.setItem(LOCALE_KEY, l);
    setLocaleState(l);
  }

  return [locale, setLocale];
}

export function resolveLocale(stored: string): string {
  return stored === SYSTEM_LOCALE ? navigator.language : stored;
}

export function formatDate(
  value: Date | string | number,
  storedLocale: string,
  options?: Intl.DateTimeFormatOptions,
): string {
  const d = value instanceof Date ? value : new Date(value);
  if (isNaN(d.getTime())) return String(value);
  const locale = resolveLocale(storedLocale);
  return new Intl.DateTimeFormat(locale, options ?? { day: '2-digit', month: '2-digit', year: 'numeric' }).format(d);
}

export function formatNumber(
  value: number,
  storedLocale: string,
  options?: Intl.NumberFormatOptions,
): string {
  const locale = resolveLocale(storedLocale);
  return new Intl.NumberFormat(locale, options).format(value);
}
