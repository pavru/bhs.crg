import { Children, forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { CircularProgress } from './CircularProgress';
import { Tooltip } from './Tooltip';
import { BASE, FILLED_SHADOW, SIZE, variantClass, type ButtonSize, type ButtonVariant } from './buttonClasses';

/**
 * MD3-кнопки (issue #110, фаза 1). Форма — пилюля (rounded-full), label-large (500),
 * слой состояния MD3 на наведение, фокус и нажатие, общее кольцо фокуса. Иерархия: **максимум одна filled
 * на экран/модалку**; заметное вторичное — tonal; обрамлённое — outlined; третичное — text.
 * Плотность компактная (по умолчанию md=36px), spec-высота 40px доступна как lg.
 */
export type { ButtonVariant, ButtonSize } from './buttonClasses';

// text-вариант живёт без заливки → чуть плотнее по горизонтали.
const TEXT_SIZE: Record<ButtonSize, string> = {
  sm: 'h-8 px-2 text-body-small',
  md: 'h-9 px-3 text-body-medium',
  lg: 'h-10 px-4 text-body-medium',
};
// multiline: фикс. высота → min-h (растёт при переносе). Литеральные строки — иначе Tailwind
// не сгенерирует классы (динамический .replace() не сканируется при сборке).
const MULTILINE_SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-8 px-3 text-body-small',
  md: 'min-h-9 px-4 text-body-medium',
  lg: 'min-h-10 px-6 text-body-medium',
};
const MULTILINE_TEXT_SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-8 px-2 text-body-small',
  md: 'min-h-9 px-3 text-body-medium',
  lg: 'min-h-10 px-4 text-body-medium',
};

/** Есть ли у кнопки подпись: `{cond && 'Удалить'}` даёт `false`, и это не подпись. */
const hasLabel = (children: ReactNode) => Children.toArray(children).some(c => c !== '');

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  danger?: boolean;
  loading?: boolean;
  /** Ведущая иконка (слева). При loading заменяется спиннером. */
  icon?: ReactNode;
  trailingIcon?: ReactNode;
  fullWidth?: boolean;
  /** Разрешить перенос длинной подписи на несколько строк (высота растёт, фикс. h → min-h).
   *  По умолчанию кнопки однострочные (whitespace-nowrap). */
  multiline?: boolean;
  /**
   * Подсказка при наведении и фокусе — наша (`Tooltip`, issue #1394), а не браузерная. У неактивной
   * кнопки она и объясняет, почему нельзя: видна и тогда. У кнопки без подписи — ещё и её имя, у
   * кнопки с подписью — постоянное описание для скринридера (`aria-description`).
   */
  title?: string;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'text', size = 'md', danger = false, loading = false,
    icon, trailingIcon, fullWidth, multiline = false, className = '', type, disabled, title, children, ...rest },
  ref,
) {
  // Однострочные по умолчанию; multiline меняет фикс. высоту на min-h + вертикальный отступ и разрешает перенос.
  const sizeCls = multiline
    ? `${(variant === 'text' ? MULTILINE_TEXT_SIZE : MULTILINE_SIZE)[size]} py-1.5 whitespace-normal`
    : `${(variant === 'text' ? TEXT_SIZE : SIZE)[size]} whitespace-nowrap`;
  const labelled = hasLabel(children);
  return (
    // Без подписи имя кнопки — её подсказка, с подписью — описание: браузерный `title` давал их сам.
    <Tooltip content={title} name={!labelled}>
      <button
        ref={ref}
        type={type ?? 'button'}
        className={`${BASE} rounded-full ${sizeCls} ${variantClass(variant, danger)}${variant === 'filled' ? ` ${FILLED_SHADOW}` : ''} ${fullWidth ? 'w-full' : ''} ${className}`}
        {...rest}
        disabled={disabled || loading}
      >
        {loading ? <CircularProgress size={18} className="shrink-0" /> : icon}
        {children}
        {trailingIcon}
      </button>
    </Tooltip>
  );
});

const ICON_SIZE: Record<ButtonSize, string> = {
  sm: 'h-8 w-8',
  md: 'h-9 w-9',
  lg: 'h-10 w-10',
};

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** Обязательная доступная подпись (иконка сама по себе непрозрачна для скринридера). Она же —
   *  подсказка при наведении, если своей `title` не дали. */
  label: string;
  title?: string;
  size?: ButtonSize;
  danger?: boolean;
}

/** Круглая icon-кнопка MD3: слой состояния, общее кольцо фокуса, обязательный aria-label. */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, size = 'md', danger = false, className = '', type, title, children, ...rest },
  ref,
) {
  // Цвет значка под курсором не меняется ([UI-64]). У опасной кнопки слой — цветом ошибки
  // (`--_state-color`): она предупреждает, что нажатие удалит, но в покое не кричит.
  const look = danger ? 'text-on-surface-variant [--_state-color:var(--color-error)]' : 'text-on-surface-variant';
  // Без своей `title` подсказка лишь повторяет имя — неактивной кнопке её показывать незачем.
  return (
    <Tooltip content={title ?? (rest.disabled ? undefined : label)}>
      <button
        ref={ref}
        type={type ?? 'button'}
        aria-label={label}
        className={`inline-flex items-center justify-center rounded-full shrink-0 ${ICON_SIZE[size]} ` +
          'state-layer focus-ring disabled-content disabled:pointer-events-none ' +
          look +
          ` ${className}`}
        {...rest}
      >
        {children}
      </button>
    </Tooltip>
  );
});
