import type { ButtonHTMLAttributes } from 'react';

interface CalendarDayButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'type' | 'children' | 'aria-pressed'> {
  day: number;
  selected: boolean;
  mark?: string;
}

/** День в сетке месяца. Сетка задаёт подпись, состояние и маршрут клавиатурного фокуса. */
export function CalendarDayButton({ day, selected, mark, className = '', ...props }: CalendarDayButtonProps) {
  return <button {...props} type="button" aria-pressed={selected}
    className={`focus-ring state-layer relative grid min-h-8 min-w-0 place-items-center rounded-sm text-label-medium
      aria-pressed:ring-2 aria-pressed:ring-inset aria-pressed:ring-primary ${className}`}>
    {day}
    {mark && <span aria-hidden="true" className="absolute right-0.5 top-0 text-label-small">{mark}</span>}
  </button>;
}
