import { forwardRef, useEffect, useRef, type InputHTMLAttributes, type ReactNode } from 'react';
import { Icon } from './Icon';
import { Check, Remove } from './icons';

/**
 * Флажок — checkbox MD3 ([UI-69], issue #1351). Под рисунком — нативный `<input type="checkbox">`: клавиатура,
 * фокус, отметка в форме, `indeterminate` и чтение диктором у него свои и верные. Сам `input` — круг 40 px
 * без своего вида (`appearance: none`) со слоем состояния; поля −11 px оставляют ему в раскладке 18 px, так
 * что строки таблиц и отступы мест не сдвигаются. Квадрат 18 px рисует соседний `span` поверх: обводка 2 px
 * `on-surface-variant`, у отмеченного и частичного — заливка `primary` со знаком `on-primary`, ошибка — роль
 * `error`. Размер один: 16 px у MD3 нет.
 *
 * `indeterminate` — состояние «отмечены не все» у флажка «выбрать всё». Атрибутом HTML его не задать,
 * только свойством элемента; диктор читает его как «частично отмечен».
 *
 * Только для чтения (`aria-readonly`) — тот же язык, что у поля: обводка `outline-variant` без заливки,
 * знак `on-surface-variant`. Правку гасит место: у флажка нет своего `readOnly`.
 *
 * С подписью флажок оборачивается в `<label>`: щелчок по тексту переключает его, и имя у флажка
 * появляется само. Без подписи имя обязан дать `aria-label`. Подпись — блок по ширине содержимого
 * ([UI-41]): два флажка подряд стоят двумя строками, как прежние блочные `<label>` экранов, а щелчок
 * в пустоте строки справа от текста флажок не переключает.
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
  /**
   * Классы следа флажка (18 px) — с подписью и без: выравнивание по строке. С подписью они заменяют её
   * собственный отступ сверху. `className` же ложится на внешний элемент — на строку `<label>`, если
   * подпись есть, иначе на след; отступ, заданный им, при появлении подписи молча переехал бы со флажка
   * на всю строку.
   */
  boxClassName?: string;
}

const on = (v: unknown) => v === true || v === 'true';

/** Квадрат по состоянию. Отключённое — по MD3: обводка и заливка on-surface 38 %, знак surface. */
function markClass({ filled, invalid, readOnly, disabled }: { filled: boolean; invalid: boolean; readOnly: boolean; disabled: boolean }) {
  if (disabled) return filled ? 'bg-on-surface/38 text-surface' : 'border-2 border-on-surface/38';
  if (readOnly) return 'border-2 border-outline-variant text-on-surface-variant';
  if (invalid) return filled ? 'bg-error text-on-error' : 'border-2 border-error';
  return filled ? 'bg-primary text-on-primary' : 'border-2 border-on-surface-variant';
}

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox(
  { checked, onChange, indeterminate = false, label, description, disabled, className = '', boxClassName, ...rest },
  ref,
) {
  const inner = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    if (inner.current) inner.current.indeterminate = indeterminate;
  }, [indeterminate]);

  const filled = checked || indeterminate;
  const invalid = on(rest['aria-invalid']);
  const readOnly = on(rest['aria-readonly']);
  // Слой — цветом знака: у отмеченного primary (error у ошибки), у пустого on-surface.
  const layer = invalid ? '[--_state-color:var(--color-error)]'
    : filled && !readOnly ? '[--_state-color:var(--color-primary)]' : '[--_state-color:var(--color-on-surface)]';

  const box = (
    <span className={`relative inline-flex shrink-0 ${boxClassName ?? (label ? 'mt-px' : '')} ${label ? '' : className}`}>
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
        // bg-transparent — у input без своего вида остаётся фон браузера (Canvas), и круг 40 был бы виден в покое.
        className={`peer -m-[11px] size-10 shrink-0 appearance-none rounded-full bg-transparent state-layer ${layer} focus-ring ` +
          `${disabled ? 'cursor-not-allowed' : 'cursor-pointer'}`}
      />
      {/* Форма 2 px — токен флажка MD3, ниже шкалы формы: исключение храповика legacy-radius названо. */}
      <span aria-hidden="true" className={`pointer-events-none absolute inset-0 grid place-items-center rounded-[2px] ` +
        markClass({ filled, invalid, readOnly, disabled: !!disabled })}>
        {indeterminate ? <Icon icon={Remove} size={18} /> : checked ? <Icon icon={Check} size={18} /> : null}
      </span>
    </span>
  );
  if (!label) return box;

  return (
    // Отключённое — подпись on-surface 38 % ([UI-64]), а не прозрачность всей строки.
    <label className={`flex w-fit items-start gap-2 text-body-medium select-none ` +
      `${disabled ? 'text-on-surface/38 cursor-not-allowed' : 'text-on-surface cursor-pointer'} ${className}`}>
      {box}
      <span className="min-w-0">
        {label}
        {description && <span className={`block text-body-small ${disabled ? '' : 'text-on-surface-variant'}`}>{description}</span>}
      </span>
    </label>
  );
});
