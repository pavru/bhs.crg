import type { DatePrecision } from '@/shared/api/types';
import type { DateMask, DatePart } from '@/shared/format/dateMask';
import { companyToday } from '@/shared/format/companyDay';
import { daysInMonth, MIN_YEAR, MAX_YEAR } from '@/shared/utils/date';
import { EMPTY_SEGMENTS, SEGMENT_PROBLEMS, fullYear, parsePasted, readSegments, segmentsOf, type Segments } from './dateSegments';
import type { SegmentModel } from './useSegmentField';

/**
 * Дата для движка сегментов (`useSegmentField`, issue #1468): сегменты в порядке языка, разбор и
 * проверка — `dateSegments`. Хранимое значение — всегда ГГГГ-ММ-ДД, скрытые точностью части — «01».
 *
 * @param mask маска видимых точностью сегментов
 * @param fullOrder порядок полной даты языка — для вставки: дату целиком вставляют и в поле месяца
 */
export function dateSegmentModel(mask: DateMask, fullOrder: DatePart[], precision: DatePrecision): SegmentModel<DatePart> {
  return {
    order: mask.order,
    sep: mask.sep,
    labels: mask.labels,
    names: mask.names,
    len: LEN,
    // День 4–9 и месяц 2–9 начаться так не могут — «0X» и дальше.
    padAbove: PAD_ABOVE,
    separators: SEPARATORS,
    empty: EMPTY_SEGMENTS,
    of: segmentsOf,
    read(s) {
      const r = readSegments(s, precision);
      return r.kind === 'date' ? { kind: 'value', value: r.iso } : r;
    },
    // Одиночная цифра дня и месяца — с нулём; «18.10.26» → 2026: раньше двузначный год при уходе
    // молча отбрасывался вместе с датой.
    fix(part, typed) {
      if (part === 'y') return typed.length === 2 ? fullYear(typed) : null;
      return typed.length === 1 ? '0' + typed : null;
    },
    step,
    paste: text => parsePasted(text, precision, fullOrder),
    text: s => readSegments(s, precision).kind === 'date' ? mask.order.map(part => s[part]).join(mask.sep) : null,
    problems: SEGMENT_PROBLEMS,
    selectedAll: 'Выделена вся дата',
  };
}

const LEN: Record<DatePart, number> = { d: 2, m: 2, y: 4 };
const PAD_ABOVE: Partial<Record<DatePart, number>> = { d: 3, m: 1 };
/** Клавиши-разделители: набрав «18.» или «10/», человек ждёт следующий сегмент при любом языке. */
const SEPARATORS = ['.', '/', '-'];

/**
 * ↑/↓ (issue #1463): день и месяц по кругу, пустой сегмент — с края (↑ — 1, ↓ — последний), пустой
 * год — год компании. День ходит по кругу своего месяца, если месяц набран: иначе ↑ с 30.11 давал бы
 * 31.11 — «такой даты нет». Год — в границах календаря; недописанный (1 или 3 цифры) стрелка не
 * трогает, чтобы не стереть набранное.
 */
function step(part: DatePart, delta: 1 | -1, shown: Segments): string | null {
  const cur = shown[part];
  if (part === 'y') {
    if (cur.length === 1 || cur.length === 3) return null;
    const year = cur === '' ? +companyToday().slice(0, 4) : +(cur.length === 2 ? fullYear(cur) : cur) + delta;
    return String(Math.min(MAX_YEAR, Math.max(MIN_YEAR, year)));
  }
  const top = part === 'm' ? 12 : dayTop(shown);
  const n = cur === '' || +cur > top ? (delta > 0 ? 1 : top) : ((+cur - 1 + delta + top) % top) + 1;
  return String(n).padStart(2, '0');
}

/**
 * Последний день для стрелок: дней в набранном месяце; год не дописан — по високосному (29 февраля
 * не отсекается раньше, чем год известен); месяца нет — 31.
 */
function dayTop({ m, y }: Segments): number {
  const month = +m;
  if (!month || month > 12) return 31;
  return daysInMonth(y.length === 4 ? +y : 2000, month);
}
