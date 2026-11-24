import type { CalendarDay, CalendarDayKind } from '@/shared/api/planningCalendars';

export const FIVE_DAYS = 31;
export const YEAR_CONFLICT = 'Год тем временем изменили. Перечитайте календарь.';
export const SHORT_WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
export const DAY_LABELS: Record<CalendarDayKind, string> = {
  holiday: 'Нерабочий день',
  working: 'Рабочий выходной',
  short: 'Сокращённый день',
};

const KIND_STYLES: Record<CalendarDayKind, string> = {
  holiday: 'bg-error-container text-on-error-container',
  working: 'bg-primary-container text-on-primary-container',
  short: 'bg-tertiary-container text-on-tertiary-container',
};
export const kindStyle = (kind: CalendarDayKind) => KIND_STYLES[kind];

export function weekLabel(week: number) {
  return SHORT_WEEKDAYS.filter((_, index) => week & (1 << index)).join(' · ');
}

export function replaceDay(days: CalendarDay[], date: string, kind: CalendarDayKind | null) {
  const rest = days.filter(day => day.date !== date);
  return (kind ? [...rest, { date, kind }] : rest).sort((a, b) => a.date.localeCompare(b.date));
}
