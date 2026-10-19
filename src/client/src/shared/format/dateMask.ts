import type { DatePrecision } from '@/shared/api/types';

/**
 * Маска ввода даты и названия календаря — по языку пользователя (issue #1461, решение владельца
 * 18.10.2026): `ru-RU` — ДД.ММ.ГГГГ, `en-US` — MM/DD/YYYY, `en-GB` — DD/MM/YYYY, `de-DE` — TT.MM.JJJJ.
 *
 * <p>Порядок сегментов и разделитель берутся у `Intl`, а не таблицей в коде: таблица разошлась бы с
 * показом дат (`format.ts`) на первом же языке, которого в ней нет. Язык здесь называют явно — его
 * передаёт компонент, подписанный на настройку, чтобы смена языка перерисовала поле сразу.</p>
 *
 * <p>Хранимое значение от языка не зависит: всегда ГГГГ-ММ-ДД. Язык меняет только ввод и показ.</p>
 */

export type DatePart = 'd' | 'm' | 'y';

export interface DateMask {
  /** Видимые точностью сегменты в порядке языка: ['m', 'd', 'y'] у en-US. */
  order: DatePart[];
  /** Разделитель между сегментами: «.» или «/». */
  sep: string;
  /** Обозначения сегментов для подсказки в пустом поле: ДД / DD / TT. */
  labels: Record<DatePart, string>;
  /** Маска текстом — для подписей и сообщений: «ДД.ММ.ГГГГ», «MM/YYYY». */
  text: string;
}

// Обозначения сегментов `Intl` не знает — это слова языка. Нет языка в таблице — английские.
const LABELS: Record<string, Record<DatePart, string>> = {
  ru: { d: 'ДД', m: 'ММ', y: 'ГГГГ' },
  de: { d: 'TT', m: 'MM', y: 'JJJJ' },
  en: { d: 'DD', m: 'MM', y: 'YYYY' },
};

const SAMPLE = new Date(2026, 9, 18);
const FULL: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' };
const PART: Partial<Record<Intl.DateTimeFormatPartTypes, DatePart>> = { day: 'd', month: 'm', year: 'y' };

const masks = new Map<string, { order: DatePart[]; sep: string }>();

/** Порядок и разделитель полной даты языка. Неизвестный язык — как у `Intl` по умолчанию. */
function fullMask(locale: string): { order: DatePart[]; sep: string } {
  let mask = masks.get(locale);
  if (!mask) {
    let parts: Intl.DateTimeFormatPart[];
    try { parts = new Intl.DateTimeFormat(locale, FULL).formatToParts(SAMPLE); }
    catch { parts = new Intl.DateTimeFormat(undefined, FULL).formatToParts(SAMPLE); }
    const order = parts.flatMap(p => PART[p.type] ? [PART[p.type]!] : []);
    const sep = parts.find(p => p.type === 'literal')?.value.trim() || '.';
    masks.set(locale, mask = { order: order.length === 3 ? order : ['d', 'm', 'y'], sep });
  }
  return mask;
}

/**
 * Маска поля даты для языка и точности. Разделитель — у ПОЛНОЙ даты, и у месяца с годом тоже: у
 * `de-DE` `Intl` пишет месяц с годом через дробь («10/2026»), а маска поля по решению владельца —
 * MM.JJJJ; один разделитель на поле при любой точности.
 */
export function dateMask(locale: string, precision: DatePrecision = 'day'): DateMask {
  const { order: full, sep } = fullMask(locale);
  const shown: DatePart[] = precision === 'year' ? ['y'] : precision === 'month' ? ['m', 'y'] : ['d', 'm', 'y'];
  const order = full.filter(p => shown.includes(p));
  const labels = LABELS[locale.slice(0, 2).toLowerCase()] ?? LABELS.en;
  return { order, sep, labels, text: order.map(p => labels[p]).join(sep) };
}

const capitalize = (s: string) => s.charAt(0).toLocaleUpperCase() + s.slice(1);

/**
 * Названия месяцев для календаря: «Октябрь» / «October»; короткие — без точки сокращения («Окт»):
 * в сетке месяцев точка лишь шум.
 */
export function monthNames(locale: string, width: 'long' | 'short'): string[] {
  const format = new Intl.DateTimeFormat(locale, { month: width });
  return Array.from({ length: 12 }, (_, i) => capitalize(format.format(new Date(2026, i, 1)).replace(/\.$/, '')));
}

// Страны, где неделя начинается с воскресенья, — на случай браузера без `Intl.Locale#getWeekInfo`
// (Firefox): тогда первый день берётся по региону языка. Без региона — понедельник (ISO 8601).
const SUNDAY_FIRST = new Set(['US', 'CA', 'MX', 'BR', 'JP', 'KR', 'IL', 'PH', 'TW', 'HK', 'IN', 'ZA', 'AU']);

/** Первый день недели языка: 1 — понедельник … 7 — воскресенье. */
export function firstDayOfWeek(locale: string): number {
  try {
    const loc = new Intl.Locale(locale) as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number }; weekInfo?: { firstDay: number };
    };
    const info = loc.getWeekInfo?.() ?? loc.weekInfo;
    if (info?.firstDay) return info.firstDay;
    return loc.region && SUNDAY_FIRST.has(loc.region) ? 7 : 1;
  } catch {
    return 1;
  }
}

/** Короткие названия дней недели, начиная с первого дня языка: «Пн … Вс» / «Sun … Sat». */
export function weekdayNames(locale: string): string[] {
  const format = new Intl.DateTimeFormat(locale, { weekday: 'short' });
  const first = firstDayOfWeek(locale);
  // 2026-10-19 — понедельник: от него дни 1..7 по ISO.
  return Array.from({ length: 7 }, (_, i) => capitalize(format.format(new Date(2026, 9, 19 + ((first - 1 + i) % 7)))));
}
