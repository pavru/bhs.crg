import { useEffect, useRef, useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { CalendarDays } from 'lucide-react';
import type { DatePrecision } from '@/shared/api/types';
import { Calendar } from './Calendar';

interface DateInputProps {
  /** Stored value: ISO YYYY-MM-DD or '' (always full ISO, независимо от точности) */
  value: string;
  /**
   * Emits full ISO YYYY-MM-DD when the visible segments are complete, '' when explicitly cleared,
   * nothing while partially typed. Скрытые точностью части допоняются '01' (issue #60):
   * 'year' → YYYY-01-01, 'month' → YYYY-MM-01, 'day' → YYYY-MM-DD.
   */
  onChange: (iso: string) => void;
  /** Точность ввода: 'day' (ДД.ММ.ГГГГ, по умолч.), 'month' (ММ.ГГГГ), 'year' (ГГГГ). */
  precision?: DatePrecision;
  /** Applied to the outer container div (border, bg, padding, focus-within:ring, width) */
  className?: string;
  disabled?: boolean;
  /** Показывать иконку-триггер календаря (docked date picker, issue #338). По умолчанию — да. */
  calendar?: boolean;
  /** Компактный режим (ячейка таблицы): иконка появляется по hover/focus, не занимает место постоянно. */
  compact?: boolean;
}

export function DateInput({ value, onChange, precision = 'day', className = '', disabled = false, calendar = true, compact = false }: DateInputProps) {
  const [d, setD] = useState('');
  const [m, setM] = useState('');
  const [y, setY] = useState('');
  const [focused, setFocused] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);

  const showMonth = precision !== 'year';
  const showDay = precision === 'day';

  const dayRef = useRef<HTMLInputElement>(null);
  const monRef = useRef<HTMLInputElement>(null);
  const yrRef  = useRef<HTMLInputElement>(null);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  // Sync from external value only when not actively editing
  useEffect(() => {
    if (focused) return;
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
    if (match) {
      setD(match[3]); setM(match[2]); setY(match[1]);
    } else if (!value) {
      setD(''); setM(''); setY('');
    }
  }, [value, focused]);

  function onFocus() {
    clearTimeout(blurTimer.current);
    setFocused(true);
  }
  function onBlur() {
    // Delay so focus transitions between segments don't flicker focused=false
    blurTimer.current = setTimeout(() => setFocused(false), 120);
  }

  // Emit полный ISO из видимых сегментов; скрытые части допоняем '01'. Пустой ввод — явная очистка.
  function emit(dd: string, mm: string, yyyy: string) {
    if (precision === 'year') {
      if (yyyy.length === 4) onChange(`${yyyy}-01-01`);
      else if (!yyyy) onChange('');
      return;
    }
    if (precision === 'month') {
      if (mm.length === 2 && yyyy.length === 4) onChange(`${yyyy}-${mm}-01`);
      else if (!mm && !yyyy) onChange('');
      return;
    }
    if (dd.length === 2 && mm.length === 2 && yyyy.length === 4) {
      onChange(`${yyyy}-${mm}-${dd}`);
    } else if (!dd && !mm && !yyyy) {
      onChange(''); // explicit clear
    }
    // partial input — don't emit, external value unchanged; will revert on blur
  }

  function advance(ref: React.RefObject<HTMLInputElement | null>) {
    ref.current?.focus();
    // Schedule select so it runs after focus
    setTimeout(() => ref.current?.select(), 0);
  }

  function handleD(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && d.length < 2) {
      setD(v); advance(monRef); emit(v, m, y);
    } else if (v.length === 1 && d === '' && +v > 3) {
      // digit 4–9 can't start a valid day — auto-pad "0X" and advance
      const p = '0' + v; setD(p); advance(monRef); emit(p, m, y);
    } else {
      setD(v); emit(v, m, y);
    }
  }

  function handleM(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 2);
    // Auto-advance only when filling in new digits (segment was not already full)
    if (v.length === 2 && m.length < 2) {
      setM(v); advance(yrRef); emit(d, v, y);
    } else if (v.length === 1 && m === '' && +v > 1) {
      // digit 2–9 can't start a valid month — auto-pad "0X" and advance
      const p = '0' + v; setM(p); advance(yrRef); emit(d, p, y);
    } else {
      setM(v); emit(d, v, y);
    }
  }

  function handleY(raw: string) {
    const v = raw.replace(/\D/g, '').slice(0, 4);
    setY(v); emit(d, m, v);
  }

  function onDayKey(e: React.KeyboardEvent) {
    if ((e.key === '.' || e.key === '/') && d) { e.preventDefault(); advance(monRef); }
  }
  function onMonKey(e: React.KeyboardEvent) {
    if (e.key === 'Backspace' && m === '') { advance(dayRef); }
    else if ((e.key === '.' || e.key === '/') && m) { e.preventDefault(); advance(yrRef); }
  }
  function onYrKey(e: React.KeyboardEvent) {
    if (e.key === 'Backspace' && y === '') { advance(monRef); }
  }

  // Pad single digits on blur — read DOM value via ref to avoid stale closure
  // (onBlur fires synchronously before React re-renders when advance() moves focus)
  function blurD() {
    const cur = dayRef.current?.value ?? d;
    if (cur.length === 1) { const p = '0' + cur; setD(p); emit(p, m, y); }
  }
  function blurM() {
    const cur = monRef.current?.value ?? m;
    if (cur.length === 1) { const p = '0' + cur; setM(p); emit(d, p, y); }
  }

  const seg = [
    'border-0 outline-none bg-transparent text-center min-w-0 p-0',
    disabled ? 'cursor-not-allowed' : 'focus:bg-brand-subtle',
    'rounded-sm tabular-nums',
  ].join(' ');

  // Alt+↓ из любого сегмента открывает календарь (штатный MD3-жест).
  function onContainerKey(e: React.KeyboardEvent) {
    if (calendar && !disabled && e.altKey && e.key === 'ArrowDown') { e.preventDefault(); setPickerOpen(true); }
  }

  const segments = (
    <div className={`flex items-center group ${className}`} onKeyDown={onContainerKey}>
      {showDay && (
        <>
          <input
            ref={dayRef} type="text" inputMode="numeric"
            placeholder="ДД" maxLength={2}
            style={{ width: '2.2ch' }}
            value={d}
            disabled={disabled}
            onChange={e => handleD(e.target.value)}
            onKeyDown={onDayKey}
            onFocus={onFocus}
            onBlur={() => { blurD(); onBlur(); }}
            className={seg}
          />
          <span className="text-fg4 select-none">.</span>
        </>
      )}
      {showMonth && (
        <>
          <input
            ref={monRef} type="text" inputMode="numeric"
            placeholder="ММ" maxLength={2}
            style={{ width: '2.2ch' }}
            value={m}
            disabled={disabled}
            onChange={e => handleM(e.target.value)}
            onKeyDown={onMonKey}
            onFocus={onFocus}
            onBlur={() => { blurM(); onBlur(); }}
            className={seg}
          />
          <span className="text-fg4 select-none">.</span>
        </>
      )}
      <input
        ref={yrRef} type="text" inputMode="numeric"
        placeholder="ГГГГ" maxLength={4}
        style={{ width: '4ch' }}
        value={y}
        disabled={disabled}
        onChange={e => handleY(e.target.value)}
        onKeyDown={onYrKey}
        onFocus={onFocus}
        onBlur={onBlur}
        className={seg}
      />
      {calendar && (
        <Popover.Trigger asChild>
          <button type="button" disabled={disabled} aria-label="Открыть календарь"
            className={`ml-1 shrink-0 flex items-center justify-center h-5 w-5 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition disabled:opacity-40 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
              compact ? 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 data-[state=open]:opacity-100' : ''}`}>
            <CalendarDays size={compact ? 13 : 14} />
          </button>
        </Popover.Trigger>
      )}
    </div>
  );

  if (!calendar) return segments;

  return (
    <Popover.Root open={pickerOpen} onOpenChange={setPickerOpen}>
      <Popover.Anchor asChild>{segments}</Popover.Anchor>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} onOpenAutoFocus={e => e.preventDefault()}
          className="z-50 rounded-2xl bg-surface border border-stroke focus:outline-none" style={{ boxShadow: 'var(--f-shadow16)' }}>
          <Calendar value={value} precision={precision}
            onSelect={iso => onChange(iso)} onClose={() => setPickerOpen(false)} />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
