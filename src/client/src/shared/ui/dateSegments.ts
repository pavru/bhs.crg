import type { DatePrecision } from '@/shared/api/types';
import { daysInMonth } from '@/shared/utils/date';

/**
 * Сегменты поля даты — чистая логика `DateInput` (issue #1460): что показать из значения, что из
 * набранного можно отдать наружу и что разобрать во вставке. Компонент только хранит набранное.
 */
export interface Segments { d: string; m: string; y: string }

export const EMPTY_SEGMENTS: Segments = { d: '', m: '', y: '' };

/** Что дают сегменты: дату, пустоту (явная очистка) или повод для ошибки под полем. */
export type SegmentsReading =
  | { kind: 'date'; iso: string }
  | { kind: 'empty' }
  | { kind: 'partial' }
  | { kind: 'impossible' };

/** Текст ошибки под полем — по поводу. «Не дописана» показывается только после ухода из поля. */
export const SEGMENT_PROBLEMS = {
  partial: 'Дата не дописана',
  impossible: 'Такой даты нет',
  foreign: 'Значение не дата — наберите дату заново',
} as const;

/**
 * Значение → сегменты для показа. Принимает ISO, в том числе со временем («2026-10-18T00:00:00» —
 * так приходят даты из части источников), и ДД.ММ.ГГГГ — так дата лежит в ячейках комплекта
 * (`ComplexCells` значение не переводит). null — значение не дата: его показывают ТЕКСТОМ, а не
 * пустым полем, иначе человек решит, что даты нет, и перепишет.
 */
export function segmentsOf(value: string | null | undefined): Segments | null {
  const v = (value ?? '').trim();
  if (v === '') return EMPTY_SEGMENTS;
  const iso = /^(\d{4})-(\d{2})-(\d{2})(?:$|[T ])/.exec(v);
  if (iso) return { d: iso[3], m: iso[2], y: iso[1] };
  const ru = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(v);
  if (ru) return { d: ru[1], m: ru[2], y: ru[3] };
  return null;
}

/**
 * Что дают видимые точностью сегменты. Скрытые части дополняются '01' (issue #60): 'year' →
 * ГГГГ-01-01, 'month' → ГГГГ-ММ-01. Несуществующая дата (31.02, месяц 13, день 39, год с ведущим
 * нулём) наружу не отдаётся: раньше проверялись только длины, и `2026-02-31` уходил в запись.
 */
export function readSegments({ d, m, y }: Segments, precision: DatePrecision): SegmentsReading {
  const showMonth = precision !== 'year';
  const showDay = precision === 'day';
  const visible = [y, showMonth ? m : '', showDay ? d : ''];
  if (visible.every(s => s === '')) return { kind: 'empty' };
  if (y.length < 4 || (showMonth && m.length < 2) || (showDay && d.length < 2)) return { kind: 'partial' };
  const year = +y, month = showMonth ? +m : 1, day = showDay ? +d : 1;
  if (year < 1000 || month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month)) return { kind: 'impossible' };
  return { kind: 'date', iso: `${y}-${showMonth ? m : '01'}-${showDay ? d : '01'}` };
}

/** Двузначный год при уходе из поля — 20ГГ: «18.10.26» → 18.10.2026. */
export function fullYear(y: string): string {
  return y.length === 2 ? '20' + y : y;
}

const pad = (s: string) => s.padStart(2, '0');

/**
 * Дата целиком из вставки — в любой сегмент. Раньше «18.10.2026» в поле дня становилось «18»:
 * сегмент режет до двух знаков. Принимает ДД.ММ.ГГГГ, ДД/ММ/ГГГГ, ДД-ММ-ГГГГ (и двузначный год),
 * ISO (и со временем), а у поля месяца — ММ.ГГГГ. null — вставлена не дата: её обрабатывает сегмент.
 * Маска по языку пользователя — задача #1461; до неё порядок «день, месяц, год».
 */
export function parsePasted(text: string, precision: DatePrecision): Segments | null {
  const t = text.trim();
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})(?:$|[T ])/.exec(t);
  if (iso) return { d: pad(iso[3]), m: pad(iso[2]), y: iso[1] };
  const dmy = /^(\d{1,2})[./-](\d{1,2})[./-](\d{4}|\d{2})$/.exec(t);
  if (dmy) return { d: pad(dmy[1]), m: pad(dmy[2]), y: fullYear(dmy[3]) };
  const my = precision !== 'day' && /^(\d{1,2})[./-](\d{4})$/.exec(t);
  if (my) return { d: '', m: pad(my[1]), y: my[2] };
  return null;
}
