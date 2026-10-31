import { useId, useState } from 'react';
import { DateInput } from './DateInput';
import { OutlinedField } from './OutlinedField';
import { fieldBox, type FieldDensity } from './fieldBox';
import type { DatePrecision } from '@/shared/api/types';

/**
 * MD3 outlined-поле даты (issue #176): сегментный ввод в маске языка (#1461) внутри той же outlined-рамки
 * с вырезом, что и TextField (#178) — общая оболочка `OutlinedField` (#565). Поскольку у даты
 * всегда видны плейсхолдеры сегментов, метка держится ПОСТОЯННО во всплывшем положении
 * (`raise="always"`, notch всегда открыт) — это штатный MD3-паттерн для полей с форматной
 * подсказкой. Фокус ловим сами: он приходит на любой из сегментов, а не на единственный input.
 */
export function DateField({
  label, value, onChange, onIncomplete, precision, required, hint, invalid, disabled, readOnly, min, max, density = 'form',
}: {
  label: string;
  value: string;
  onChange: (iso: string) => void;
  /** Набранное — не дата, и наружу не ушло: см. `DateInput.onIncomplete` (ревью PR #1556). */
  onIncomplete?: (message: string | null) => void;
  precision?: DatePrecision;
  required?: boolean;
  hint?: string;
  invalid?: boolean;
  disabled?: boolean;
  /** Значение показано, но не правится (issue #958) — см. `DateInput.readOnly`. Приглушением НЕ
   *  отмечается: поле не отключено, а отличается обводкой `outline-variant` и отсутствием календаря,
   *  как соседние read-only поля формы. */
  readOnly?: boolean;
  /** Поле формы 56 (по умолчанию) или плотное 40 без выреза — [UI-69]. */
  density?: FieldDensity;
  /** Границы допустимых дат — календарь не даст выбрать за ними (#1464); ошибку набранного называет форма. */
  min?: string;
  max?: string;
}) {
  const [focused, setFocused] = useState(false);
  const hintId = useId();
  // Ошибка набранного (issue #1460) — под рамкой, красным. Подсказка формы при этом не теряется: она
  // бывает её же ошибкой (отказ проводки по дате у оплаты), и пропасть ей нельзя.
  const [problem, setProblem] = useState<string | null>(null);
  const error = problem ? (hint ? `${problem}. ${hint[0].toUpperCase()}${hint.slice(1)}` : problem) : undefined;
  return (
    <OutlinedField label={label} required={required} invalid={invalid} hint={hint} error={error} hintId={hintId}
      raise="always" focused={focused} readOnly={readOnly} density={density} markInside>
      {/* Отключённое — по MD3 ([UI-64]): текст on-surface 38 %, а не прозрачность всего поля. */}
      <div data-disabled={disabled ? '' : undefined}
        className={`rounded-xs flex items-center text-on-surface disabled-content ${fieldBox(density)}`}
        onFocus={() => setFocused(true)}
        onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false); }}>
        {/* Во всю ширину: хвост поля — крестик и календарь — стоит у правого края рамки (#1463). */}
        <DateInput label={label} value={value} onChange={onChange} onProblem={setProblem} onIncomplete={onIncomplete} precision={precision}
          disabled={disabled} readOnly={readOnly} required={required} className="w-full" min={min} max={max}
          hintId={hint || problem ? hintId : undefined} />
      </div>
    </OutlinedField>
  );
}
