import { useState } from 'react';
import { DateInput } from './DateInput';
import type { DatePrecision } from '@/shared/api/types';

/**
 * MD3 outlined-поле даты (issue #176): сегментный ввод ДД.ММ.ГГГГ внутри той же outlined-рамки
 * с вырезом, что и TextField (#178). Поскольку у даты всегда видны плейсхолдеры сегментов,
 * метка держится ПОСТОЯННО во всплывшем положении (notch всегда открыт) — это штатный MD3-паттерн
 * для полей с форматной подсказкой. Фон прозрачный → корректно на любом фоне.
 */
export function DateField({
  label, value, onChange, precision, required, hint, invalid, disabled,
}: {
  label: string;
  value: string;
  onChange: (iso: string) => void;
  precision?: DatePrecision;
  required?: boolean;
  hint?: string;
  invalid?: boolean;
  disabled?: boolean;
}) {
  const [focused, setFocused] = useState(false);
  const borderColor = invalid ? 'border-danger' : focused ? 'border-brand' : 'border-stroke-strong';
  const labelColor = invalid ? 'text-danger' : focused ? 'text-brand' : 'text-fg4';
  return (
    <div>
      <div className="relative"
        onFocus={() => setFocused(true)}
        onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false); }}>
        <div className={`h-14 rounded-md px-4 flex items-center text-sm text-fg1 ${disabled ? 'opacity-50' : ''}`}>
          <DateInput value={value} onChange={onChange} precision={precision} disabled={disabled} />
        </div>
        <fieldset aria-hidden
          className={`pointer-events-none absolute inset-x-0 bottom-0 -top-2 m-0 rounded-md border px-3 transition-colors ${borderColor} ${focused ? 'border-2' : ''}`}>
          <legend className="h-2.5 w-auto max-w-full whitespace-nowrap p-0 text-xs">
            <span className="inline-block px-1 opacity-0">{label}{required ? ' *' : ''}</span>
          </legend>
        </fieldset>
        <span className={`absolute left-3 top-[-8px] px-1 text-xs pointer-events-none transition-colors ${labelColor} block max-w-[calc(100%-1.5rem)] truncate`}>
          {label}{required && <span className="ml-0.5 text-danger">*</span>}
        </span>
      </div>
      {hint && <p className="mt-1 px-1 text-xs text-fg4">{hint}</p>}
    </div>
  );
}
