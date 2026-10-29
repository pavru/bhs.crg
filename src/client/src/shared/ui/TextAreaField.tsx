import { forwardRef, useId, useState, type TextareaHTMLAttributes } from 'react';
import { OutlinedField } from './OutlinedField';

/**
 * MD3 outlined-поле для многострочного текста (issue #574) — тот же `OutlinedField`, что у
 * `TextField`, но с `<textarea>` внутри.
 *
 * Заведено ради последней легаси-обёртки «подпись сверху» в редакторе реквизитов: после перевода
 * enum-полей на `Select` в слое формы в ней оставался ровно один потребитель — текстовая область.
 * Пока она жила, в одной форме соседствовали подпись в вырезе (строка, число, дата, перечисление)
 * и подпись над полем (текст).
 *
 * Метка поднята постоянно (`raise="always"`), хотя у `<textarea>` `:placeholder-shown` работает и
 * всплывание по peer-селекторам было бы технически возможно: в режиме `auto` метка покоится по
 * ЦЕНТРУ поля, а у области в три строки центр — это середина текста. Фокус поэтому приходит пропом,
 * как у даты и пикеров.
 */
export interface TextAreaFieldProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  error?: string;
  /** Красная рамка без сообщения (сообщение об ошибке рисует вызывающий код). */
  invalid?: boolean;
  /** Вспомогательный текст под полем — скрывается при наличии error. */
  hint?: string;
  containerClassName?: string;
}

export const TextAreaField = forwardRef<HTMLTextAreaElement, TextAreaFieldProps>(function TextAreaField(
  { label, error, invalid, hint, className = '', containerClassName = '', id, required, rows = 3,
    onFocus, onBlur, ...rest },
  ref,
) {
  const [focused, setFocused] = useState(false);
  const autoId = useId();
  const areaId = id ?? autoId;

  return (
    <OutlinedField
      label={label} required={required} invalid={invalid} error={error} hint={hint}
      raise="always" focused={focused} readOnly={rest.readOnly} htmlFor={areaId} containerClassName={containerClassName}
    >
      {/* Отступ под метку — padding ОБЁРТКИ, а не самого поля и не его margin.
          - Не padding `<textarea>`: она обрезает содержимое по padding-box, и при прокрутке строки
            проезжают сквозь верхний отступ и перечёркивают метку в вырезе рамки (видно на «Домены
            для тиров поиска», где строк больше двенадцати).
          - Не `mt-4` у `<textarea>`, как было: у оболочки поля нет ни padding, ни рамки, и отступ
            первого ребёнка схлопывался с её отступом — уходил НАРУЖУ. Вниз сдвигалось всё поле
            вместе с меткой, а первая строка оставалась прижатой к ней: метка заходила на текст на
            5 px (#1356, замер на живой странице).
          Padding не схлопывается: прокручиваемая область начинается на 16 px ниже верха рамки. */}
      <div className="pt-4">
        <textarea
          ref={ref} id={areaId} required={required} rows={rows}
          className={`block w-full rounded-sm bg-transparent px-4 pb-2 text-body-medium text-on-surface ` +
            `outline-none disabled-content ${className}`}
          onFocus={e => { setFocused(true); onFocus?.(e); }}
          onBlur={e => { setFocused(false); onBlur?.(e); }}
          {...rest}
        />
      </div>
    </OutlinedField>
  );
});
