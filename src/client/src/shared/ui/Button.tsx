import { Children, forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { Loader2 } from 'lucide-react';
import { Tooltip } from './Tooltip';

/**
 * MD3-кнопки (issue #110, фаза 1). Форма — пилюля (rounded-full), label-large (500),
 * state-layer на hover/active, видимое кольцо фокуса. Иерархия: **максимум одна filled
 * на экран/модалку**; заметное вторичное — tonal; обрамлённое — outlined; третичное — text.
 * Плотность компактная (по умолчанию md=36px), spec-высота 40px доступна как lg.
 */
export type ButtonVariant = 'filled' | 'tonal' | 'outlined' | 'text';
export type ButtonSize = 'sm' | 'md' | 'lg';

const BASE =
  'inline-flex items-center justify-center gap-2 rounded-full font-medium select-none ' +
  'transition-[background-color,box-shadow,filter,color,border-color] duration-150 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 ' +
  'focus-visible:ring-offset-base disabled:opacity-40 disabled:pointer-events-none';

const SIZE: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-xs',
  md: 'h-9 px-4 text-sm',
  lg: 'h-10 px-6 text-sm',
};
// text-вариант живёт без заливки → чуть плотнее по горизонтали.
const TEXT_SIZE: Record<ButtonSize, string> = {
  sm: 'h-8 px-2 text-xs',
  md: 'h-9 px-3 text-sm',
  lg: 'h-10 px-4 text-sm',
};
// multiline: фикс. высота → min-h (растёт при переносе). Литеральные строки — иначе Tailwind
// не сгенерирует классы (динамический .replace() не сканируется при сборке).
const MULTILINE_SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-8 px-3 text-xs',
  md: 'min-h-9 px-4 text-sm',
  lg: 'min-h-10 px-6 text-sm',
};
const MULTILINE_TEXT_SIZE: Record<ButtonSize, string> = {
  sm: 'min-h-8 px-2 text-xs',
  md: 'min-h-9 px-3 text-sm',
  lg: 'min-h-10 px-4 text-sm',
};

function variantClass(variant: ButtonVariant, danger: boolean): string {
  switch (variant) {
    case 'filled':
      return danger
        ? 'bg-danger text-on-danger shadow-[var(--md-sys-elevation-level1)] hover:brightness-95 active:brightness-90'
        : 'bg-brand text-on-brand shadow-[var(--md-sys-elevation-level1)] hover:bg-brand-hover active:bg-brand-pressed';
    case 'tonal':
      return 'bg-tonal text-on-tonal hover:brightness-[.97] active:brightness-95';
    case 'outlined':
      return danger
        ? 'border border-stroke-strong text-danger hover:bg-danger/10 active:bg-danger/15'
        : 'border border-stroke-strong text-brand hover:bg-brand/10 active:bg-brand/15';
    case 'text':
      return danger
        ? 'text-danger hover:bg-danger/10 active:bg-danger/15'
        : 'text-brand hover:bg-brand/10 active:bg-brand/15';
  }
}

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
        className={`${BASE} ${sizeCls} ${variantClass(variant, danger)} ${fullWidth ? 'w-full' : ''} ${className}`}
        {...rest}
        disabled={disabled || loading}
      >
        {loading ? <Loader2 size={16} className="animate-spin shrink-0" /> : icon}
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

/** Круглая icon-кнопка MD3: state-layer на hover, кольцо фокуса, обязательный aria-label. */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, size = 'md', danger = false, className = '', type, title, children, ...rest },
  ref,
) {
  const look = danger
    ? 'text-fg3 hover:text-danger hover:bg-danger/10'
    : 'text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10';
  // Без своей `title` подсказка лишь повторяет имя — неактивной кнопке её показывать незачем.
  return (
    <Tooltip content={title ?? (rest.disabled ? undefined : label)}>
      <button
        ref={ref}
        type={type ?? 'button'}
        aria-label={label}
        className={`inline-flex items-center justify-center rounded-full shrink-0 ${ICON_SIZE[size]} ` +
          'transition-[background-color,color] duration-150 focus-visible:outline-none focus-visible:ring-2 ' +
          'focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-base ' +
          'disabled:opacity-40 disabled:pointer-events-none ' +
          look +
          ` ${className}`}
        {...rest}
      >
        {children}
      </button>
    </Tooltip>
  );
});
