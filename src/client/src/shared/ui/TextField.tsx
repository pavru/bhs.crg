import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react';

/**
 * MD3 outlined-текстовое поле (issue #110, фаза 2e) с плавающей подписью: подпись сидит
 * внутри поля, когда оно пустое и без фокуса, и всплывает в вырез рамки при фокусе/заполнении.
 * Фокус — рамка primary + inset-кольцо (без сдвига раскладки). Тема light/dark через токены.
 *
 * Требует placeholder=" " (пробел) — по нему :placeholder-shown отличает пустое поле.
 * Фон подписи (bg-surface) «прорезает» рамку — поле должно лежать на surface (карточка/модалка).
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
  const bad = !!error || invalid;
  const border = bad
    ? 'border-danger focus:border-danger focus:ring-1 focus:ring-inset focus:ring-danger'
    : 'border-stroke-strong focus:border-brand focus:ring-1 focus:ring-inset focus:ring-brand';
  const labelFocus = bad ? 'peer-focus:text-danger' : 'peer-focus:text-brand';
  return (
    <div className={containerClassName}>
      <div className="relative">
        <input
          ref={ref} id={inputId} placeholder=" " required={required}
          className={`peer w-full h-12 rounded-md border bg-surface text-sm text-fg1 px-3 pt-4 pb-1 ` +
            `outline-none transition-colors disabled:opacity-50 ${trailing ? 'pr-10' : ''} ${border} ${className}`}
          {...rest}
        />
        <label
          htmlFor={inputId}
          className={`absolute left-2.5 top-3.5 px-1 text-sm bg-surface text-fg4 pointer-events-none transition-all ` +
            `peer-focus:top-[-7px] peer-focus:text-xs ${labelFocus} ` +
            `peer-[:not(:placeholder-shown)]:top-[-7px] peer-[:not(:placeholder-shown)]:text-xs`}
        >
          {label}{required && <span className="ml-0.5 text-danger">*</span>}
        </label>
        {trailing && <div className="absolute right-2 top-1/2 -translate-y-1/2">{trailing}</div>}
      </div>
      {error
        ? <p className="mt-1 px-1 text-xs text-danger">{error}</p>
        : hint ? <p className="mt-1 px-1 text-xs text-fg4">{hint}</p> : null}
    </div>
  );
});
