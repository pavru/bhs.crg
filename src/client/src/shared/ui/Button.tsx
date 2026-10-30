import { Children, forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { CircularProgress } from './CircularProgress';
import { Icon } from './Icon';
import { Tooltip } from './Tooltip';
import { BASE, FILLED_HOVER_SHADOW, HEIGHT, LABEL, MIN_HEIGHT, padding, variantClass, type ButtonVariant } from './buttonClasses';
import type { FilledIconDef, IconDef } from './icons/types';

export type { ButtonVariant } from './buttonClasses';

/** Есть ли у кнопки подпись: `{cond && 'Удалить'}` даёт `false`, и это не подпись. */
const hasLabel = (children: ReactNode) => Children.toArray(children).some(c => c !== '');

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  danger?: boolean;
  loading?: boolean;
  /** Ведущая иконка (слева), 18 px. При loading заменяется индикатором хода. */
  icon?: ReactNode;
  trailingIcon?: ReactNode;
  fullWidth?: boolean;
  /** Разрешить перенос длинной подписи на несколько строк (высота растёт: 40 — нижняя граница).
   *  По умолчанию кнопки однострочные (whitespace-nowrap). Отступление от MD3 (docs/UI.md). */
  multiline?: boolean;
  /**
   * Подсказка при наведении и фокусе — наша (`Tooltip`, issue #1394), а не браузерная. У неактивной
   * кнопки она и объясняет, почему нельзя: видна и тогда. У кнопки без подписи — ещё и её имя, у
   * кнопки с подписью — постоянное описание для скринридера (`aria-description`).
   */
  title?: string;
}

/**
 * Common button MD3 ([UI-68]): 40 px, форма full, подпись label-large, знак 18. Четыре вида —
 * **максимум одна filled на экран или окно**; заметное вторичное — tonal; обрамлённое — outlined;
 * третичное — text. Filled без тени, уровень 1 — при наведении. Размера нет: кнопкам плотность MD3 не
 * применяется. `danger` (роль `error`) и `multiline` — названные отступления (docs/UI.md).
 */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'text', danger = false, loading = false,
    icon, trailingIcon, fullWidth, multiline = false, className = '', type, disabled, title, children, ...rest },
  ref,
) {
  const leading = loading || !!icon;
  // Однострочные по умолчанию; multiline меняет фикс. высоту на нижнюю границу и разрешает перенос.
  const sizeCls = multiline
    ? `${MIN_HEIGHT} py-1.5 whitespace-normal`
    : `${HEIGHT} whitespace-nowrap`;
  const labelled = hasLabel(children);
  return (
    // Без подписи имя кнопки — её подсказка, с подписью — описание: браузерный `title` давал их сам.
    <Tooltip content={title} name={!labelled}>
      <button
        ref={ref}
        type={type ?? 'button'}
        className={`${BASE} touch-target rounded-full ${LABEL} ${sizeCls} ${padding(variant, { leading, trailing: !!trailingIcon })} ${variantClass(variant, danger)}${variant === 'filled' ? ` ${FILLED_HOVER_SHADOW}` : ''} ${fullWidth ? 'w-full' : ''} ${className}`}
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

export type IconButtonVariant = 'standard' | 'filled' | 'tonal' | 'outlined';

/** Переключатель заливает знак — значит, знак обязан иметь вариант с заливкой (`FilledIconDef`). */
type Glyph =
  | { icon: FilledIconDef; selected?: boolean }
  | { icon: IconDef; selected?: undefined };

export type IconButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> & Glyph & {
  /** Обязательная доступная подпись (иконка сама по себе непрозрачна для скринридера). Она же —
   *  подсказка при наведении, если своей `title` не дали. */
  label: string;
  title?: string;
  variant?: IconButtonVariant;
  danger?: boolean;
  /** Ход операции: знак заменяется индикатором, кнопка недоступна. */
  loading?: boolean;
  /** Цвет знака ролью, когда он сам называет состояние (`text-error` у ошибки, `text-primary` у
   *  привязанного). Только цвет: размер знака задаёт кнопка. */
  iconClassName?: string;
  children?: never;
};

// Размер — переменная места ([UI-68], В7): клетка DataGrid и EditGrid ставит 32 (XS M3 Expressive) при
// мыши и 40 при касании (shared/ui/dataTable.ts), везде прочее — 40. Порталы лежат в body и переменную
// не наследуют: окно из клетки остаётся 40. Литералами — Tailwind не сканирует собранные строки.
const SIZE = 'size-[var(--md-comp-icon-button-size,40px)]';
const GLYPH = 'size-[var(--md-comp-icon-button-icon,24px)] shrink-0';

/** Цвет вида: без `selected` — обычная кнопка, с ним — переключатель (MD3, design «Виды IconButton»). */
function look(variant: IconButtonVariant, selected: boolean | undefined): string {
  switch (variant) {
    case 'standard':
      return `${selected ? 'text-primary' : 'text-on-surface-variant'} disabled-content`;
    case 'filled':
      return selected === false
        ? 'bg-surface-container-highest text-primary disabled-container'
        : 'bg-primary text-on-primary disabled-container';
    case 'tonal':
      return selected === false
        ? 'bg-surface-container-highest text-on-surface-variant disabled-container'
        : 'bg-secondary-container text-on-secondary-container disabled-container';
    case 'outlined':
      return selected
        ? 'bg-inverse-surface text-inverse-on-surface disabled-container'
        : 'border border-outline text-on-surface-variant disabled-outlined';
  }
}

/**
 * Icon button MD3 ([UI-68]): 40×40 со знаком 24, в клетке таблицы — по размеру места. Знак кнопка рисует
 * сама (`icon`). С `selected` — переключатель: `aria-pressed` и залитый знак. Слой состояния, общее
 * кольцо фокуса, обязательный aria-label.
 */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, variant = 'standard', selected, danger = false, loading = false, icon, iconClassName = '',
    className = '', type, title, disabled, ...rest },
  ref,
) {
  // Цвет значка под курсором не меняется ([UI-64]). У опасной кнопки слой — цветом ошибки
  // (`--_state-color`): она предупреждает, что нажатие удалит, но в покое не кричит.
  const state = danger ? ' [--_state-color:var(--color-error)]' : '';
  const off = disabled || loading;
  // Без своей `title` подсказка лишь повторяет имя — неактивной кнопке её показывать незачем.
  return (
    <Tooltip content={title ?? (off ? undefined : label)}>
      <button
        ref={ref}
        type={type ?? 'button'}
        aria-label={label}
        aria-pressed={selected}
        className={`inline-flex items-center justify-center rounded-full shrink-0 ${SIZE} ` +
          'state-layer focus-ring touch-target disabled:pointer-events-none ' +
          look(variant, selected) + state +
          ` ${className}`}
        {...rest}
        disabled={off}
      >
        {loading
          ? <CircularProgress size={24} className={GLYPH} />
          : selected
            ? <Icon icon={icon as FilledIconDef} filled size={24} className={`${GLYPH} ${iconClassName}`} />
            : <Icon icon={icon} size={24} className={`${GLYPH} ${iconClassName}`} />}
      </button>
    </Tooltip>
  );
});
