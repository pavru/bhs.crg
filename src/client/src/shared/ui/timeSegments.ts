import { timeMask, type TimePart } from '@/shared/format/timeMask';
import type { SegmentModel, SegmentReading, SegmentValues } from './useSegmentField';

export type { TimePart };

/**
 * Время суток ЧЧ:ММ для движка сегментов (issue #1468). Всегда 24 часа, при любом языке и любой ОС:
 * сегмента AM/PM нет — «04:44 AM» при английской Windows задача и закрывает. Хранимое значение —
 * ровно «ЧЧ:ММ», его ждёт сервер (`BackupScheduleService.ParseTimeOfDay`).
 */
export type TimeSegments = SegmentValues<TimePart>;

export const EMPTY_TIME: TimeSegments = { h: '', min: '' };

export const TIME_PROBLEMS = {
  partial: 'Время не дописано',
  impossible: 'Такого времени нет',
  foreign: 'Значение не время — наберите время заново',
} as const;

/** «ЧЧ:ММ» (и с секундами — так время отдают часть источников) → сегменты; null — значение не время. */
export function timeSegmentsOf(value: string | null | undefined): TimeSegments | null {
  const v = (value ?? '').trim();
  if (v === '') return EMPTY_TIME;
  const t = /^(\d{2}):(\d{2})(?::\d{2})?$/.exec(v);
  return t ? { h: t[1], min: t[2] } : null;
}

/** Что дают сегменты: время, пустоту или повод для ошибки. 24:00 и 12:60 — «такого времени нет». */
export function readTime({ h, min }: TimeSegments): SegmentReading {
  if (h === '' && min === '') return { kind: 'empty' };
  if (h.length < 2 || min.length < 2) return { kind: 'partial' };
  if (+h > 23 || +min > 59) return { kind: 'impossible' };
  return { kind: 'value', value: `${h}:${min}` };
}

/**
 * Время текстом — вставка в любой сегмент. Принимается то, что встречается в письмах и таблицах:
 * «4:44», «04.44», «4,44», «0444», «444», с секундами («16:44:30» — секунды ниже точности поля) и с
 * AM/PM («4:44 PM» → 16:44, «12:05 AM» → 00:05): вставку из английского письма переводим, а не
 * отвергаем. «16:44 PM» даёт 28:44 — и ошибку «такого времени нет», а не молчаливые 16:44. null —
 * вставлено не время: его разбирает сегмент.
 */
export function parseTime(text: string): TimeSegments | null {
  const t = text.trim().toLowerCase();
  const parts = /^(\d{1,2})[:.,](\d{2})(?:[:.,]\d{2})?\s*(?:([ap])\.?\s*m\.?)?$/.exec(t)
    ?? /^(\d{1,2})(\d{2})\s*(?:([ap])\.?\s*m\.?)?$/.exec(t);
  if (!parts) return null;
  let hour = +parts[1];
  if (parts[3] === 'a' && hour === 12) hour = 0;
  else if (parts[3] === 'p' && hour !== 12) hour += 12;
  return { h: String(hour).padStart(2, '0'), min: parts[2] };
}

const models = new Map<string, SegmentModel<TimePart>>();

/**
 * Модель времени языка: разделитель, обозначения и имена — `timeMask` (язык пользователя). Хранимое
 * значение от языка не зависит.
 */
export function timeSegmentModel(locale: string): SegmentModel<TimePart> {
  let model = models.get(locale);
  if (model) return model;
  const mask = timeMask(locale);
  model = {
    order: ['h', 'min'],
    sep: mask.sep,
    labels: mask.labels,
    names: mask.names,
    len: { h: 2, min: 2 },
    // Часа 30 и минуты 60 не бывает: «3» в часах и «6» в минутах — сразу «03» / «06» и дальше.
    padAbove: { h: 2, min: 5 },
    // «.» — потому что «:» в русской раскладке набирают с Shift, а «4.44» — привычная запись; «,» даёт
    // точка цифрового блока в русской раскладке.
    separators: [':', '.', ','],
    empty: EMPTY_TIME,
    of: timeSegmentsOf,
    read: readTime,
    fix: (_part, typed) => typed.length === 1 ? '0' + typed : null,
    step,
    paste: parseTime,
    text: s => readTime(s).kind === 'value' ? `${s.h}${model!.sep}${s.min}` : null,
    problems: TIME_PROBLEMS,
    selectedAll: 'Выделено всё время',
  };
  models.set(locale, model);
  return model;
}

/**
 * ↑/↓: часы по кругу 00–23, минуты 00–59, без переноса в час — как у нативного поля. Пустой сегмент —
 * с края: ↑ ставит 00, ↓ — 23 или 59.
 */
function step(part: TimePart, delta: 1 | -1, shown: TimeSegments): string {
  const top = part === 'h' ? 23 : 59;
  const cur = shown[part];
  const n = cur === '' || +cur > top ? (delta > 0 ? 0 : top) : (+cur + delta + top + 1) % (top + 1);
  return String(n).padStart(2, '0');
}
