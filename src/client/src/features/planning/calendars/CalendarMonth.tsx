import { useState, type KeyboardEvent } from 'react';
import type { CalendarDayKind } from '@/shared/api/planningCalendars';
import { monthNames, spokenDate } from '@/shared/format/dateMask';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';
import { CalendarDayButton } from '@/shared/ui/CalendarDayButton';
import { Card } from '@/shared/ui/Card';
import { daysInMonth, monthMatrix, toISO } from '@/shared/utils/date';
import { DAY_LABELS, kindStyle, SHORT_WEEKDAYS } from './calendarDays';

const MOVEMENT: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 };
const MARKS: Record<CalendarDayKind, string> = { holiday: '×', working: '+', short: '−' };

export function CalendarMonth({ year, month, selected, days, week, onSelect }: {
  year: number; month: number; selected: string; days: ReadonlyMap<string, CalendarDayKind>;
  week: number; onSelect: (date: string) => void;
}) {
  const [locale] = useLocale();
  const language = resolveLocale(locale);
  const title = monthNames(language, 'long')[month - 1];
  const cells = monthMatrix(year, month, 1).flat();
  const [focus, setFocus] = useState(1);

  function move(event: KeyboardEvent<HTMLButtonElement>, day: number) {
    const delta = MOVEMENT[event.key];
    if (delta === undefined) return;
    event.preventDefault();
    const target = Math.max(1, Math.min(daysInMonth(year, month), day + delta));
    event.currentTarget.parentElement?.querySelector<HTMLButtonElement>(`[data-day="${target}"]`)?.focus();
  }

  return <Card title={<span className="capitalize">{title}</span>} className="min-w-0" flush>
    <div className="grid grid-cols-7 gap-1 p-2 pt-0 text-center tabular-nums">
      {SHORT_WEEKDAYS.map(day => <span key={day} className="text-label-small text-on-surface-variant">{day}</span>)}
      {cells.map((day, cell) => {
        if (day === null) return <span key={cell} aria-hidden="true" />;
        const date = toISO(year, month, day);
        const kind = days.get(date);
        const weekend = !(week & (1 << (cell % 7)));
        const label = kind ? DAY_LABELS[kind] : weekend ? 'Выходной' : 'Рабочий день';
        return <CalendarDayButton key={cell} day={day} selected={date === selected} data-day={day}
          tabIndex={day === focus ? 0 : -1} aria-label={`${spokenDate(date, language)}, ${label}`}
          mark={kind ? MARKS[kind] : undefined} onFocus={() => setFocus(day)}
          onKeyDown={event => move(event, day)} onClick={() => onSelect(date)}
          className={kind ? kindStyle(kind) : weekend ? 'bg-surface-container text-on-surface-variant' : ''} />;
      })}
    </div>
  </Card>;
}
