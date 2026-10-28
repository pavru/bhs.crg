import { forwardRef, useEffect, useRef, type InputHTMLAttributes, type ReactNode } from 'react';

/**
 * Флажок (issue #1351). Нативный `<input type="checkbox">`, а не нарисованный: клавиатура, фокус,
 * отметка в форме и чтение экранным диктором у него свои и верные. Цвет — `accent-primary`, размер —
 * 18 px (MD3) или 16 px в плотных строках.
 *
 * `indeterminate` — состояние «отмечены не все» у флажка «выбрать всё». Атрибутом HTML его не задать,
 * только свойством элемента; диктор читает его как «частично отмечен».
 *
 * С подписью флажок оборачивается в `<label>`: щелчок по тексту переключает его, и имя у флажка
 * появляется само. Без подписи имя обязан дать `aria-label`.
 */
export interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange' | 'size'> {
  checked: boolean;
  onChange: (checked: boolean) => void;
  /** Отмечены не все: флажок «выбрать всё» при частичном выборе. */
  indeterminate?: boolean;
  /** Подпись справа от флажка. */
  label?: ReactNode;
  /** Пояснение под подписью. */
  description?: ReactNode;
  /** `sm` — 16 px, для строк таблиц и плотных списков. */
  size?: 'md' | 'sm';
}

const BOX: Record<NonNullable<CheckboxProps['size']>, string> = {
  md: 'w-[18px] h-[18px]',
  sm: 'w-4 h-4',
};

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox(
  { checked, onChange, indeterminate = false, label, description, size = 'md', disabled, className = '', ...rest },
  ref,
) {
  const inner = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    if (inner.current) inner.current.indeterminate = indeterminate;
  }, [indeterminate]);

  const input = (
    <input
      {...rest}
      ref={el => {
        inner.current = el;
        if (typeof ref === 'function') ref(el);
        else if (ref) ref.current = el;
      }}
      type="checkbox"
      checked={checked}
      disabled={disabled}
      onChange={e => onChange(e.target.checked)}
      className={`${BOX[size]} shrink-0 accent-primary ${disabled ? 'cursor-not-allowed' : 'cursor-pointer'} ` +
        `focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary ${label ? (size === 'md' ? 'mt-px' : 'mt-0.5') : className}`}
    />
  );
  if (!label) return input;

  return (
    <label className={`inline-flex items-start gap-2 text-sm text-on-surface select-none ` +
      `${disabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'} ${className}`}>
      {input}
      <span className="min-w-0">
        {label}
        {description && <span className="block text-xs text-fg3">{description}</span>}
      </span>
    </label>
  );
});
