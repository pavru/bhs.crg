import { useState } from 'react';
import { DateInput } from './DateInput';
import { OutlinedField } from './OutlinedField';
import type { DatePrecision } from '@/shared/api/types';

/**
 * MD3 outlined-поле даты (issue #176): сегментный ввод ДД.ММ.ГГГГ внутри той же outlined-рамки
 * с вырезом, что и TextField (#178) — общая оболочка `OutlinedField` (#565). Поскольку у даты
 * всегда видны плейсхолдеры сегментов, метка держится ПОСТОЯННО во всплывшем положении
 * (`raise="always"`, notch всегда открыт) — это штатный MD3-паттерн для полей с форматной
 * подсказкой. Фокус ловим сами: он приходит на любой из сегментов, а не на единственный input.
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
  return (
    <OutlinedField label={label} required={required} invalid={invalid} hint={hint}
      raise="always" focused={focused}>
      <div className={`h-14 rounded-md px-4 flex items-center text-sm text-fg1 ${disabled ? 'opacity-50' : ''}`}
        onFocus={() => setFocused(true)}
        onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false); }}>
        <DateInput value={value} onChange={onChange} precision={precision} disabled={disabled} />
      </div>
    </OutlinedField>
  );
}
