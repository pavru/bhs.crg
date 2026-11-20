/**
 * Числа ленты периодов (`Timeline.tsx`): окно по неделям и место полосы в нём — отдельно от отрисовки, для теста.
 * Даты — ISO без времени, счёт — днями от эпохи по UTC: пояс браузера дату не сдвигает.
 */

export const DAY = 86_400_000;

/** День от эпохи — по UTC, чтобы пояс браузера не сдвигал дату. */
const dayOf = (iso: string) => Math.floor(Date.UTC(+iso.slice(0, 4), +iso.slice(5, 7) - 1, +iso.slice(8, 10)) / DAY);
export const isoOf = (day: number) => new Date(day * DAY).toISOString().slice(0, 10);

/** Первый день недели, в которую попадает день, — по языку пользователя. */
function weekStart(day: number, firstDay: number): number {
  const weekday = new Date(day * DAY).getUTCDay(); // 0 — воскресенье
  return day - ((weekday - (firstDay % 7) + 7) % 7);
}

/** Числа окна: первая неделя, дни, положение «сегодня» — отдельно от отрисовки, для теста. */
export function timelineWindow(today: string, offsetWeeks: number, weeks: number, firstDay = 1) {
  const start = weekStart(dayOf(today), firstDay) + (offsetWeeks - 2) * 7;
  const days = weeks * 7;
  return { start, days, end: start + days - 1, todayAt: dayOf(today) - start };
}

/** Где лежит полоса в окне: доли от его ширины и обрезка с краёв; `null` — целиком вне окна. */
export function barPlace(from: string, to: string | null, start: number, days: number) {
  const a = dayOf(from) - start;
  const b = to === null ? Infinity : dayOf(to) - start;
  if (b < 0 || a > days - 1) return null;
  const left = Math.max(0, a);
  const right = Math.min(days - 1, b);
  return { left: left / days, width: (right - left + 1) / days, cutStart: a < 0, cutEnd: b > days - 1, open: to === null };
}
