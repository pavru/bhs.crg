import { createContext, useContext } from 'react';

/**
 * Ключ ЗЕРКАЛА в браузере (issue #953): сам выбор лежит на сервере и приезжает на другой
 * компьютер, здесь остаётся последнее известное значение — чтобы до ответа сервера форматировать
 * не наугад.
 */
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

/**
 * Язык форматирования — ОДИН на приложение (issue #953). Значение держит `LocaleProvider`, а
 * хранит сервер.
 *
 * Раньше язык читал каждый потребитель сам, своим `useState` из localStorage, и значений было
 * столько же, сколько потребителей: сменив язык в настройках, человек видел новый формат дат там
 * — и старый в разделе копий, пока тот не перемонтируется.
 *
 * ⚠️ Контекст объявлен ЗДЕСЬ, вместе с константами, а не в соседнем файле рядом с провайдером:
 * отдельный модуль импортировал бы отсюда `SYSTEM_LOCALE`, а этот модуль — контекст оттуда, и
 * кольцо импортов роняло бы приложение целиком («Cannot access 'SYSTEM_LOCALE' before
 * initialization») — при зелёных типах и зелёных тестах. Поймано живым прогоном.
 */
export const LocaleContext = createContext<[string, (value: string) => void]>([SYSTEM_LOCALE, () => {}]);

/** Выбранный язык и способ его сменить. */
export function useLocale(): [string, (locale: string) => void] {
  return useContext(LocaleContext);
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
