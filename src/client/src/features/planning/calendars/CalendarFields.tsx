import type { CalendarDayKind } from '@/shared/api/planningCalendars';
import { Banner } from '@/shared/ui/Banner';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { DAY_LABELS, kindStyle, SHORT_WEEKDAYS } from './calendarDays';

const WEEKDAYS = ['Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота', 'Воскресенье'];
export function WeekFields({ value, onChange, disabled = false }: {
  value: number; onChange: (week: number) => void; disabled?: boolean;
}) {
  return <fieldset className="grid gap-2" disabled={disabled}>
    <legend className="mb-2 text-label-large">Рабочие дни недели</legend>
    <div className="flex flex-wrap gap-x-5 gap-y-3">{WEEKDAYS.map((day, index) => {
      const mask = 1 << index;
      return <Checkbox key={day} label={SHORT_WEEKDAYS[index]} aria-label={day} checked={Boolean(value & mask)}
        disabled={disabled || value === mask} onChange={checked => onChange(checked ? value | mask : value & ~mask)} />;
    })}</div>
  </fieldset>;
}

export function DayLabel({ kind, fallback }: { kind?: CalendarDayKind | null; fallback: string }) {
  return kind ? <span className={`inline-block rounded-sm px-2 py-1 text-label-medium ${kindStyle(kind)}`}>
    {DAY_LABELS[kind]}
  </span> : <span className="text-body-medium text-on-surface-variant">{fallback}</span>;
}

export function DayKindField({ value, onChange, baselineLabel, disabled }: {
  value: CalendarDayKind | null; onChange: (value: CalendarDayKind | null) => void; baselineLabel: string; disabled?: boolean;
}) {
  return <Select label="Вид дня" value={value ?? 'default'} disabled={disabled}
    onValueChange={next => onChange(next === 'default' ? null : next as CalendarDayKind)}>
    <SelectItem value="default">{baselineLabel}</SelectItem>
    {Object.entries(DAY_LABELS).map(([kind, label]) => <SelectItem key={kind} value={kind}>{label}</SelectItem>)}
  </Select>;
}

export function CalendarWriteRefusal({ error, conflict, onReload }: {
  error: unknown; conflict: string; onReload: () => void;
}) {
  if (!error) return null;
  const stale = isConflict(error);
  return <Banner tone={stale ? 'warning' : 'error'}
    actions={stale ? [{ label: 'Перечитать', onClick: onReload }] : []}>
    {stale ? conflict : apiError(error, 'Календарь не удалось сохранить.')}
  </Banner>;
}
