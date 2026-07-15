import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react';

/**
 * MD3 outlined-текстовое поле (issue #110/#178) с плавающей подписью. Реализован НАСТОЯЩИЙ
 * вырез рамки (MD3 notch) через fieldset+legend: контейнер прозрачный, верхняя рамка реально
 * прерывается под меткой. Благодаря этому поле корректно выглядит на ЛЮБОМ фоне (surface/base/
 * карточка), а не только на surface. Тема light/dark — через токены.
 *
 * Требует placeholder=" " (пробел) — по нему :placeholder-shown отличает пустое поле.
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
  const borderColor = bad
    ? 'border-danger peer-focus:border-danger'
    : 'border-stroke-strong peer-focus:border-brand';
  const labelColor = bad ? 'text-danger peer-focus:text-danger' : 'text-fg4 peer-focus:text-brand';

  return (
    <div className={containerClassName}>
      <div className="relative">
        <input
          ref={ref} id={inputId} placeholder=" " required={required}
          className={`peer w-full h-12 rounded-md bg-transparent text-sm text-fg1 px-3 ` +
            `outline-none disabled:opacity-50 ${trailing ? 'pr-10' : ''} ${className}`}
          {...rest}
        />
        {/* Рамка с вырезом: fieldset даёт границу, legend прорезает верх под плавающей меткой. */}
        <fieldset aria-hidden
          className={`pointer-events-none absolute inset-x-0 bottom-0 -top-2 m-0 rounded-md border px-2 transition-colors ${borderColor} peer-focus:border-2`}>
          <legend className="ml-1 h-2.5 w-auto max-w-[0.01px] whitespace-nowrap p-0 text-xs invisible transition-[max-width] duration-100 peer-focus:max-w-full peer-[:not(:placeholder-shown)]:max-w-full">
            <span className="inline-block px-1 opacity-0">{label}{required ? ' *' : ''}</span>
          </legend>
        </fieldset>
        <label
          htmlFor={inputId}
          className={`absolute left-2 top-1/2 -translate-y-1/2 px-1 text-sm pointer-events-none transition-all ${labelColor} ` +
            `block max-w-[calc(100%-1rem)] truncate ` +
            `peer-focus:top-[-8px] peer-focus:text-xs peer-[:not(:placeholder-shown)]:top-[-8px] peer-[:not(:placeholder-shown)]:text-xs`}
        >
          {label}{required && <span className="ml-0.5 text-danger">*</span>}
        </label>
        {trailing && <div className="absolute right-2 top-1/2 -translate-y-1/2 z-10">{trailing}</div>}
      </div>
      {error
        ? <p className="mt-1 px-1 text-xs text-danger">{error}</p>
        : hint ? <p className="mt-1 px-1 text-xs text-fg4">{hint}</p> : null}
    </div>
  );
});
