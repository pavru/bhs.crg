import type { Assignment } from '@/shared/api/workAssignments';
import { formatDate } from '@/shared/format/format';

/**
 * Строки панели «Назначения» (change `work-12-assignments-ui`, макет Р2 А+): подпись периода, состояние и отбор.
 * «Сегодня» — стройки, из ответа (`construction.today`); «действует» — поле сервера `active`. Клиент сравнивает
 * только даты строки с этим «сегодня» — чтобы разделить «будет» и «закончилось», которых сервер не называет.
 */

export type AssignmentPhase = 'active' | 'future' | 'past';

/** Без «сегодня» стройки (пояс не определён) — только то, что сказал сервер: действует или нет. */
export function phaseOf(a: Assignment, today: string | null): AssignmentPhase | 'inactive' {
  if (a.active) return 'active';
  if (!today) return 'inactive';
  return a.dateFrom > today ? 'future' : 'past';
}

/** «01.11.2026 — 30.11.2026»; у менеджера без конца — «с 01.09.2026, без срока». */
export function periodLabel(a: Pick<Assignment, 'dateFrom' | 'dateTo'>): string {
  return a.dateTo === null ? `с ${formatDate(a.dateFrom)}, без срока` : `${formatDate(a.dateFrom)} — ${formatDate(a.dateTo)}`;
}

/** Подпись полосы ленты — короче: без года у закрытого периода. */
export function barLabel(a: Pick<Assignment, 'dateFrom' | 'dateTo'>): string {
  const short = (iso: string) => formatDate(iso).slice(0, 5);
  return a.dateTo === null ? `с ${formatDate(a.dateFrom)}, без срока` : `${short(a.dateFrom)} — ${short(a.dateTo)}`;
}

export function phaseLabel(a: Assignment, today: string | null): string {
  switch (phaseOf(a, today)) {
    case 'active': return 'действует';
    case 'future': return `с ${formatDate(a.dateFrom)}`;
    case 'past': return 'закончилось';
    default: return 'не действует';
  }
}

/** Отбор панели: `current` — действующие и будущие, пусто — все. */
export type AssignmentFilter = 'current' | '';

/**
 * Что показать под отбором и сколько закончившихся он скрыл. Без «сегодня» стройки отбор не скрывает ничего:
 * «закончилось» не определить, а спрятать действующее молча хуже, чем показать лишнее.
 */
export function filterAssignments(items: Assignment[], filter: AssignmentFilter, today: string | null) {
  if (filter !== 'current' || !today) return { shown: items, hidden: 0 };
  const shown = items.filter(a => phaseOf(a, today) !== 'past');
  return { shown, hidden: items.length - shown.length };
}
