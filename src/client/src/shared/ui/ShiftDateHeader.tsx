import { useId } from 'react';
import { DateField } from './DateField';
import { DarkMode } from './icons';
import { Tabs } from './Tabs';

export function ShiftDateHeader({ date, shift, today, locked, busy, lockReason, onDate, onShift, onIncomplete }: {
  date: string;
  shift: 'day' | 'night';
  today?: string;
  locked: boolean;
  busy: boolean;
  lockReason?: string;
  onDate: (date: string) => void;
  onShift: (shift: 'day' | 'night') => void;
  onIncomplete: (message: string | null) => void;
}) {
  const id = useId();
  return <>
    <DateField label="Дата смены" required value={date} today={today} readOnly={locked} disabled={busy}
      onChange={onDate} onIncomplete={onIncomplete} />
    <div className="min-w-0 space-y-1">
      <Tabs id={id} variant="segmented" aria-label="Смена" value={shift} onChange={onShift}
        items={[{ value: 'day', label: 'День', disabled: locked || busy },
          { value: 'night', label: 'Ночь', icon: DarkMode, disabled: locked || busy }]} />
      {lockReason && <p className="text-body-small text-on-surface-variant">{lockReason}</p>}
    </div>
  </>;
}
