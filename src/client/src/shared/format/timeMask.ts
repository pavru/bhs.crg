/**
 * Маска ввода времени суток ЧЧ:ММ по языку пользователя (issue #1468) — как `dateMask` у даты:
 * разделитель у `Intl` с 24 часами (у части языков это точка), обозначения ЧЧ / HH, имена «час» /
 * «минута» для скринридера. Всегда 24 часа: сегмента AM/PM у поля нет.
 *
 * <p>Здесь, а не у поля: форматирование по месту сторожит `format.census.test.ts`.</p>
 */

export type TimePart = 'h' | 'min';

export interface TimeMask {
  /** Разделитель часов и минут: «:» или «.». */
  sep: string;
  /** Обозначения пустых сегментов: ЧЧ / HH. */
  labels: Record<TimePart, string>;
  /** Имена сегментов для чтения с экрана: «час», «минута» / «hour». */
  names: Record<TimePart, string>;
}

// Обозначения сегментов `Intl` не знает — это слова языка. Нет языка в таблице — английские.
const LABELS: Record<string, Record<TimePart, string>> = {
  ru: { h: 'ЧЧ', min: 'ММ' },
  en: { h: 'HH', min: 'MM' },
};
const NAMES_EN: Record<TimePart, string> = { h: 'hour', min: 'minute' };
const SAMPLE = new Date(2026, 9, 18, 16, 44);
const masks = new Map<string, TimeMask>();

export function timeMask(locale: string): TimeMask {
  let mask = masks.get(locale);
  if (!mask) {
    masks.set(locale, mask = {
      sep: separatorOf(locale),
      labels: LABELS[locale.split('-')[0]] ?? LABELS.en,
      names: namesOf(locale),
    });
  }
  return mask;
}

function separatorOf(locale: string): string {
  try {
    const parts = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(SAMPLE);
    return parts.find(p => p.type === 'literal')?.value.trim() || ':';
  } catch { return ':'; }
}

function namesOf(locale: string): Record<TimePart, string> {
  try {
    const of = new Intl.DisplayNames(locale, { type: 'dateTimeField' });
    return { h: of.of('hour') ?? NAMES_EN.h, min: of.of('minute') ?? NAMES_EN.min };
  } catch { return NAMES_EN; }
}
