const DAY_MS = 86_400_000;

export const dayOf = (date: string) => Date.parse(`${date}T00:00:00Z`) / DAY_MS;
export const dateAt = (day: number) => new Date(day * DAY_MS).toISOString().slice(0, 10);

export const FIRST_DAY = dayOf('0001-01-01');
export const LAST_DAY = dayOf('9999-12-31');
export const RANGE_DAYS = 31;

export function rangeStart(day: number) {
  return Math.max(FIRST_DAY, Math.min(day, LAST_DAY - RANGE_DAYS + 1));
}

export function visibleWorkDays(work: { start: string | null; end: string | null }, from: number) {
  if (!work.start || !work.end) return null;
  const start = Math.max(0, dayOf(work.start) - from);
  const end = Math.min(RANGE_DAYS, dayOf(work.end) - from + 1);
  return end > start ? { start, end } : null;
}
