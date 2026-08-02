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
      raise="always" focused={focused} htmlFor={areaId} containerClassName={containerClassName}
    >
      <textarea
        ref={ref} id={areaId} required={required} rows={rows}
        // Отступ под метку — ВНЕШНИЙ (`mt-4`), а не верхний padding. `<textarea>` обрезает
        // содержимое по padding-box, поэтому при прокрутке строки проезжают сквозь верхний padding
        // и перечёркивают метку, сидящую в вырезе рамки (видно на «Домены для тиров поиска», где
        // строк больше двенадцати). С внешним отступом прокручиваемая область начинается ниже
        // метки, и наехать на неё нечему.
        className={`block w-full rounded-md bg-transparent mt-4 px-4 pb-2 text-sm text-fg1 ` +
          `outline-none disabled:opacity-50 ${className}`}
        onFocus={e => { setFocused(true); onFocus?.(e); }}
        onBlur={e => { setFocused(false); onBlur?.(e); }}
        {...rest}
      />
    </OutlinedField>
  );
});
