import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react';
import { OutlinedField } from './OutlinedField';
import { fieldBox, fieldDescribedBy, type FieldDensity } from './fieldBox';

/**
 * MD3 outlined-текстовое поле (issue #110/#178) с плавающей подписью. Рамку с настоящим вырезом,
 * метку и подпись под полем рисует общая оболочка `OutlinedField` (#565) — здесь остаётся сам
 * `<input>` и его состояния.
 *
 * Требует placeholder=" " (пробел) — по нему :placeholder-shown отличает пустое поле, и на этом же
 * держится всплывание метки в режиме `raise="auto"`.
 */
export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'placeholder'> {
  label: string;
  error?: string;
  /** Красная рамка без сообщения (сообщение об ошибке рисует вызывающий код). */
  invalid?: boolean;
  /** Вспомогательный текст под полем (напр. пример ввода) — скрывается при наличии error. */
  hint?: string;
  /** Правый адорнмент (например «глаз» пароля). */
  trailing?: ReactNode;
  /** Поле формы 56 (по умолчанию) или плотное 40 без выреза — [UI-69], см. `fieldBox`. */
  density?: FieldDensity;
  containerClassName?: string;
  /**
   * Невидимое дополнение к полю внутри его оболочки — живая область читалки (`SuggestField`). Рядом с
   * полем, а не соседом в разметке экрана: сосед сделал бы поле «не последним» в стопке `space-y-*`.
   */
  children?: ReactNode;
}

export const TextField = forwardRef<HTMLInputElement, TextFieldProps>(function TextField(
  { label, error, invalid, hint, trailing, density = 'form', className = '', containerClassName = '', id, required, children,
    'aria-describedby': describedBy, ...rest },
  ref,
) {
  const autoId = useId();
  const inputId = id ?? autoId;
  // Ошибку и пояснение под рамкой диктор читает вместе с полем (issue #1468, [UI-69]).
  const hintId = `${inputId}-hint`;
  const described = fieldDescribedBy(hintId, error || hint, describedBy);

  return (
    <OutlinedField
      label={label} required={required} invalid={invalid} error={error} hint={hint} hintId={hintId}
      readOnly={rest.readOnly} density={density} htmlFor={inputId} trailing={trailing} containerClassName={containerClassName}
    >
      <input
        ref={ref} id={inputId} placeholder=" " required={required} aria-describedby={described}
        className={`peer w-full rounded-xs bg-transparent text-on-surface ${fieldBox(density, { trailing: !!trailing })} ` +
          `outline-none disabled-content ${className}`}
        {...rest}
      />
      {children}
    </OutlinedField>
  );
});
