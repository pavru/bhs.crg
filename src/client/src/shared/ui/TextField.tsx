import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react';
import { OutlinedField } from './OutlinedField';

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
  containerClassName?: string;
}

export const TextField = forwardRef<HTMLInputElement, TextFieldProps>(function TextField(
  { label, error, invalid, hint, trailing, className = '', containerClassName = '', id, required, ...rest },
  ref,
) {
  const autoId = useId();
  const inputId = id ?? autoId;

  return (
    <OutlinedField
      label={label} required={required} invalid={invalid} error={error} hint={hint}
      htmlFor={inputId} trailing={trailing} containerClassName={containerClassName}
    >
      <input
        ref={ref} id={inputId} placeholder=" " required={required}
        className={`peer w-full h-14 rounded-md bg-transparent text-sm text-fg1 px-4 ` +
          `outline-none disabled:opacity-50 ${trailing ? 'pr-11' : ''} ${className}`}
        {...rest}
      />
    </OutlinedField>
  );
});
