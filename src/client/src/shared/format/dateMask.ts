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

/**
 * Дата из хранимого ГГГГ-ММ-ДД в записи МАСКИ поля — так, как её набирают: «01.07.2026» у ru-RU,
 * «07/01/2026» у en-US, «07.2026» при точности «месяц». Для текста, который обязан совпасть с полем
 * рядом: «Дата не ранее …» под полем, сводка свёрнутого объекта. Не ISO — как есть.
 */
export function inMask(iso: string, locale: string, precision: DatePrecision = 'day'): string {
  const g = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!g) return iso;
  const v: Record<DatePart, string> = { y: g[1], m: g[2], d: g[3] };
  const { order, sep } = dateMask(locale, precision);
  return order.map(p => v[p]).join(sep);
}

const capitalize = (s: string, locale: string) => s.charAt(0).toLocaleUpperCase(locale) + s.slice(1);

/**
 * Названия месяцев для календаря: «Октябрь» / «October». Короткие — для сетки месяцев: три первые
 * буквы, если по ним месяцы различимы («Янв … Сен … Дек», как было до #1461), иначе сокращения
 * языка без точки — у fr-FR «juin» и «juillet» на трёх буквах слились бы.
 */
export function monthNames(locale: string, width: 'long' | 'short'): string[] {
  const names = (w: 'long' | 'short') => {
    const format = new Intl.DateTimeFormat(locale, { month: w });
    return Array.from({ length: 12 }, (_, i) => capitalize(format.format(new Date(2026, i, 1)).replace(/\.$/, ''), locale));
  };
  const long = names('long');
  if (width === 'long') return long;
  const three = long.map(n => n.slice(0, 3));
  return new Set(three).size === 12 ? three : names('short');
}

// Первый день недели по региону — для браузера без `Intl.Locale#getWeekInfo` (Firefox). Списки —
// данные CLDR (их же отдаёт `getWeekInfo`; сверяет тест по всем регионам). Остальные — понедельник.
const SUNDAY_FIRST = new Set(('AG AS BD BR BS BT BW BZ CA CO DM DO ET GT GU HK HN ID IL IN IS JM JP KE KH KR '
  + 'LA MH MM MO MT MX MZ NI NP PA PE PH PK PR PT PY SA SG SV TH TT TW UM US VE VI WS YE ZA ZW').split(' '));
const SATURDAY_FIRST = new Set('AF BH DJ DZ EG IQ IR JO KW LY OM QA SD SY'.split(' '));

/** Первый день недели языка: 1 — понедельник … 7 — воскресенье. */
export function firstDayOfWeek(locale: string): number {
  try {
    const loc = new Intl.Locale(locale) as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number }; weekInfo?: { firstDay: number };
    };
    const info = loc.getWeekInfo?.() ?? loc.weekInfo;
    if (info?.firstDay) return info.firstDay;
    // Язык без региона («en» у «Системной») — регион по умолчанию для языка: en → US.
    const region = loc.region ?? loc.maximize().region ?? '';
    return SUNDAY_FIRST.has(region) ? 7 : SATURDAY_FIRST.has(region) ? 6 : region === 'MV' ? 5 : 1;
  } catch {
    return 1;
  }
}

/** Короткие названия дней недели, начиная с первого дня языка: «Пн … Вс» / «Sun … Sat». */
export function weekdayNames(locale: string): string[] {
  const format = new Intl.DateTimeFormat(locale, { weekday: 'short' });
  const first = firstDayOfWeek(locale);
  // 2026-10-19 — понедельник: от него дни 1..7 по ISO.
  return Array.from({ length: 7 }, (_, i) => capitalize(format.format(new Date(2026, 9, 19 + ((first - 1 + i) % 7))), locale));
}
