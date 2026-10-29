import { useId, useState } from 'react';
import { OutlinedField } from './OutlinedField';
import { TimeInput } from './TimeInput';

/**
 * MD3 outlined-поле времени ЧЧ:ММ (issue #1468, макет — вариант А) — слой формы, как `DateField`:
 * рамка h-14 с вырезом, метка всегда наверху (у сегментов всегда видны обозначения ЧЧ:ММ), подпись
 * под полем. Ошибка набранного — под рамкой, вместе с подписью: подпись бывает поясом, по которому
 * время считается, и пропасть ей нельзя.
 */
export function TimeField({ label, value, onChange, onIncomplete, required, hint, invalid, disabled, className }: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  /** Набранное — не время, и наружу не ушло: см. `TimeInput.onIncomplete`. */
  onIncomplete?: (message: string | null) => void;
  required?: boolean;
  hint?: string;
  invalid?: boolean;
  disabled?: boolean;
  /** Классы внешней обёртки: ширина и место в раскладке. */
  className?: string;
}) {
  const [focused, setFocused] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const hintId = useId();
  const error = problem ? (hint ? `${problem}. ${hint}` : problem) : undefined;
  return (
    <OutlinedField label={label} required={required} invalid={invalid} hint={hint} error={error} hintId={hintId}
      raise="always" focused={focused} containerClassName={className}>
      <div className={`h-14 rounded-sm px-4 flex items-center text-body-medium text-on-surface ${disabled ? 'opacity-50' : ''}`}
        onFocus={() => setFocused(true)}
        onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false); }}>
        <TimeInput label={label} value={value} onChange={onChange} onProblem={setProblem} onIncomplete={onIncomplete}
          disabled={disabled} required={required} hintId={hint || problem ? hintId : undefined} className="w-full" />
      </div>
    </OutlinedField>
  );
}
