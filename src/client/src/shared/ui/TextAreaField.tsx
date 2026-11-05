import { forwardRef, useId, useState, type TextareaHTMLAttributes } from 'react';
import { OutlinedField } from './OutlinedField';
import { fieldDescribedBy, fieldEnd, type FieldTrailingProps } from './fieldBox';

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
interface TextAreaFieldOwnProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  error?: string;
  /** Красная рамка без сообщения (сообщение об ошибке рисует вызывающий код). */
  invalid?: boolean;
  /** Вспомогательный текст под полем — скрывается при наличии error. */
  hint?: string;
  containerClassName?: string;
}

/**
 * Хвост (`FieldTrailingProps`) — знаки и кнопка значения из источника ([UI-86]); `trailingMark` — перед кнопкой
 * ещё знак 18 (замок запертого поля). Слот оболочки стоит по первой строке, а не посреди области, и текст всех
 * строк уступает ему место справа: хвост — не плавающий над текстом знак.
 */
export type TextAreaFieldProps = TextAreaFieldOwnProps & FieldTrailingProps;

export const TextAreaField = forwardRef<HTMLTextAreaElement, TextAreaFieldProps>(function TextAreaField(
  { label, error, invalid, hint, trailing, trailingMark, className = '', containerClassName = '', id, required, rows = 3,
    onFocus, onBlur, 'aria-describedby': describedBy, ...rest },
  ref,
) {
  const [focused, setFocused] = useState(false);
  const autoId = useId();
  const areaId = id ?? autoId;
  const hintId = `${areaId}-hint`;
  const described = fieldDescribedBy(hintId, error || hint, describedBy);

  return (
    <OutlinedField
      label={label} required={required} invalid={invalid} error={error} hint={hint} hintId={hintId}
      raise="always" focused={focused} readOnly={rest.readOnly} htmlFor={areaId} containerClassName={containerClassName}
      trailing={trailing} trailingMark={trailingMark}
    >
      {/* Отступ под метку — padding ОБЁРТКИ, а не самого поля и не его margin.
          - Не padding `<textarea>`: она обрезает содержимое по padding-box, и при прокрутке строки
            проезжают сквозь верхний отступ и перечёркивают метку в вырезе рамки (видно на «Домены
            для тиров поиска», где строк больше двенадцати).
          - Не `mt-4` у `<textarea>`, как было: у оболочки поля нет ни padding, ни рамки, и отступ
            первого ребёнка схлопывался с её отступом — уходил НАРУЖУ. Вниз сдвигалось всё поле
            вместе с меткой, а первая строка оставалась прижатой к ней: метка заходила на текст на
            5 px (#1356, замер на живой странице).
          Padding не схлопывается: прокручиваемая область начинается на 16 px ниже верха рамки.
          Место хвоста — тоже padding обёртки, а не области: полоса прокрутки рисуется у края самой
          `<textarea>`, и при отступе внутри неё она уходила бы под кнопку хвоста — щелчок по стрелке
          прокрутки попадал бы в кнопку. Без хвоста отступ остаётся у области, как был. */}
      <div className={`pt-4 ${trailing ? fieldEnd('form', { trailing: true, trailingMark }) : ''}`}>
        <textarea
          ref={ref} id={areaId} required={required} rows={rows} aria-describedby={described}
          aria-invalid={invalid || !!error || undefined}
          className={`block w-full rounded-xs bg-transparent pl-4 ${trailing ? 'pr-1' : fieldEnd('form')} pb-2 text-body-large text-on-surface ` +
            `outline-none disabled-content ${className}`}
          onFocus={e => { setFocused(true); onFocus?.(e); }}
          onBlur={e => { setFocused(false); onBlur?.(e); }}
          {...rest}
        />
      </div>
    </OutlinedField>
  );
});
