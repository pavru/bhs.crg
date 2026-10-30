import { Icon } from './Icon';
import { Check, Close, KeyboardArrowDown, type IconDef } from './icons';
import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { Tooltip } from './Tooltip';

/**
 * Чип-переключатель (issue #1351): готовый отбор, быстрый фильтр, пометка «выбрано» — пилюля MD3.
 * Собран по самодельным чипам, которые уже жили на экранах (строка «Навести порядок», избранные
 * стройки, очередь счетов), чтобы новые экраны не заводили четвёртый вариант.
 *
 * - `selected` задан — чип переключатель: `aria-pressed`, значок сменяется галочкой.
 * - `tone` — вид НЕвыбранного чипа: `danger` зовёт чинить, `warning` — проверить, `neutral` — тихий.
 *   Выбранный всегда в цвете бренда: «отбор стоит» важнее, чем то, о чём он.
 * - `dashed` — пунктир у невыбранного: место под условие, которого ещё нет.
 * - `count` — число строк под отбором; ноль рисуется как есть, решать, показывать ли чип, — экрану.
 * - `onRemove` — крестик снятия отдельной кнопкой рядом (своё имя для диктора: `removeLabel`).
 * - `removeOnly` (с `onRemove`) — жетон значения: нажать в нём нечего, кроме крестика, и текст —
 *   не кнопка, а подпись; `selected` и `tone` только красят (#1469). Крестик снятия в пилюле — это он,
 *   а не `IconButton`: кнопка-иконка — 32 px, и пилюля выросла бы на треть.
 * - `menu` — чип открывает выбор (поповер, меню): стрелка справа, `selected` только красит — нажатым
 *   он не бывает, состояние называет подпись («Избранные стройки» / «Все стройки», #1432).
 * - `role="radio"` — чип один из группы взаимоисключающих (`role="radiogroup"` у обёртки, стрелки и
 *   переход фокуса — у неё): `aria-checked` вместо `aria-pressed`, иначе диктор назвал бы три
 *   независимых переключателя, а не «один из трёх» (#1455).
 *
 * - `title` — подсказка нашим `Tooltip` у самой кнопки, как у `Button`: у неактивного чипа она и объясняет,
 *   почему нельзя. Внешняя обёртка `<Tooltip><Chip disabled>` этого не умеет — `aria-disabled` вместо
 *   `disabled` подсказка ставит только своей кнопке, и причина пропадала (ревью #1704).
 *
 * Чипы отбора над таблицей с правкой условия на месте — `shared/filter/FilterChips`: там чип — лицо
 * условия с поповером, а не переключатель.
 */
export interface ChipProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  children: ReactNode;
  /** На саму кнопку: радиогруппа переводит фокус стрелками. */
  ref?: Ref<HTMLButtonElement>;
  selected?: boolean;
  icon?: IconDef;
  count?: ReactNode;
  tone?: 'neutral' | 'warning' | 'danger';
  dashed?: boolean;
  onRemove?: () => void;
  removeLabel?: string;
  menu?: boolean;
  removeOnly?: boolean;
}

const BASE = 'inline-flex items-center gap-1 rounded-full border text-body-small bg-surface';
/**
 * Состояния — у кнопки, а не у пилюли: у чипа с крестиком их две, и подсвечивается та, что под курсором.
 * Рамку отключённого гасит только чип-кнопка: у половинок чипа с крестиком своей рамки нет.
 */
const PRESS = 'state-layer focus-ring disabled-content disabled:pointer-events-none';

// Наведение — слоем цвета подписи ([UI-64]): своей заливки и смены цвета у тона нет.
const TONE: Record<NonNullable<ChipProps['tone']>, string> = {
  neutral: 'border-outline-variant text-on-surface-variant',
  warning: 'border-outline-variant text-warning',
  danger: 'border-error text-error',
};

const SELECTED = 'border-primary text-on-primary-container bg-primary-container';

function chipClass({ selected = false, tone = 'neutral', dashed = false }:
  Pick<ChipProps, 'selected' | 'tone' | 'dashed'> = {}) {
  return `${BASE} ${selected ? SELECTED : `${TONE[tone]}${dashed ? ' border-dashed' : ''}`}`;
}

export function Chip({ children, selected, icon, count, tone, dashed, onRemove, removeLabel, menu = false, removeOnly = false, title, className = '', type = 'button', ...rest }: ChipProps) {
  const toggles = selected !== undefined && !menu && !removeOnly;
  const radio = rest.role === 'radio';
  const state = radio ? { 'aria-checked': !!selected } : { 'aria-pressed': toggles ? selected : undefined };
  const shown = toggles && selected ? Check : icon;
  const body = (
    <>
      {shown && <Icon icon={shown} size={18} aria-hidden className="shrink-0" />}
      <span className="min-w-0 truncate">{children}</span>
      {count !== undefined && <span className="font-medium tabular-nums">· {count}</span>}
      {menu && <Icon icon={KeyboardArrowDown} size={18} aria-hidden className="shrink-0" />}
    </>
  );

  if (!onRemove)
    return (
      <Tooltip content={title}>
        <button {...rest} type={type} {...state}
          className={`${chipClass({ selected, tone, dashed })} ${PRESS} disabled-outlined px-2.5 py-1 ${className}`}>
          {body}
        </button>
      </Tooltip>
    );

  const label = removeOnly
    // Атрибуты данных (`data-*`) — подписи, а не кнопке, которой нет; ref и обработчики жетону не нужны.
    ? <span {...Object.fromEntries(Object.entries(rest).filter(([k]) => k.startsWith('data-')))}
        className="inline-flex items-center gap-1 pl-2.5 pr-1 py-1 min-w-0">{body}</span>
    : <Tooltip content={title}>
        <button {...rest} type={type} {...state}
          className={`inline-flex items-center gap-1 pl-2.5 pr-1 py-1 rounded-l-full min-w-0 ${PRESS}`}>
          {body}
        </button>
      </Tooltip>;

  return (
    <span className={`${chipClass({ selected, tone, dashed })} ${className}`}>
      {label}
      <Tooltip content={removeLabel ?? 'Снять'}>
        <button type="button" onClick={onRemove} aria-label={removeLabel ?? 'Снять'}
          disabled={rest.disabled}
          className={`pl-0.5 pr-2 py-1 rounded-r-full ${PRESS} [--_state-color:var(--color-error)]`}>
          <Icon icon={Close} size={18} aria-hidden />
        </button>
      </Tooltip>
    </span>
  );
}
