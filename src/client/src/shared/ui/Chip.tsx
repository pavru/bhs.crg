import { Check, ChevronDown, X, type LucideIcon } from 'lucide-react';
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
 * Чипы отбора над таблицей с правкой условия на месте — `shared/filter/FilterChips`: там чип — лицо
 * условия с поповером, а не переключатель.
 */
export interface ChipProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  children: ReactNode;
  /** На саму кнопку: радиогруппа переводит фокус стрелками. */
  ref?: Ref<HTMLButtonElement>;
  selected?: boolean;
  icon?: LucideIcon;
  count?: ReactNode;
  tone?: 'neutral' | 'warning' | 'danger';
  dashed?: boolean;
  onRemove?: () => void;
  removeLabel?: string;
  menu?: boolean;
  removeOnly?: boolean;
}

const BASE = 'inline-flex items-center gap-1 rounded-full border text-xs bg-surface transition-colors ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50 disabled:pointer-events-none';

const TONE: Record<NonNullable<ChipProps['tone']>, string> = {
  neutral: 'border-outline-variant text-fg3 hover:text-on-surface hover:border-outline',
  warning: 'border-warning-border text-warning hover:bg-warning-container',
  danger: 'border-error text-error hover:bg-error-container',
};

const SELECTED = 'border-primary text-primary bg-primary-container';

function chipClass({ selected = false, tone = 'neutral', dashed = false }:
  Pick<ChipProps, 'selected' | 'tone' | 'dashed'> = {}) {
  return `${BASE} ${selected ? SELECTED : `${TONE[tone]}${dashed ? ' border-dashed' : ''}`}`;
}

export function Chip({ children, selected, icon: Icon, count, tone, dashed, onRemove, removeLabel, menu = false, removeOnly = false, className = '', type = 'button', ...rest }: ChipProps) {
  const toggles = selected !== undefined && !menu && !removeOnly;
  const radio = rest.role === 'radio';
  const state = radio ? { 'aria-checked': !!selected } : { 'aria-pressed': toggles ? selected : undefined };
  const Shown = toggles && selected ? Check : Icon;
  const body = (
    <>
      {Shown && <Shown size={12} aria-hidden className="shrink-0" />}
      <span className="min-w-0 truncate">{children}</span>
      {count !== undefined && <span className="font-medium tabular-nums">· {count}</span>}
      {menu && <ChevronDown size={12} aria-hidden className="shrink-0" />}
    </>
  );

  if (!onRemove)
    return (
      <button {...rest} type={type} {...state}
        className={`${chipClass({ selected, tone, dashed })} px-2.5 py-1 ${className}`}>
        {body}
      </button>
    );

  const label = removeOnly
    // Атрибуты данных (`data-*`) — подписи, а не кнопке, которой нет; ref и обработчики жетону не нужны.
    ? <span {...Object.fromEntries(Object.entries(rest).filter(([k]) => k.startsWith('data-')))}
        className="inline-flex items-center gap-1 pl-2.5 pr-1 py-1 min-w-0">{body}</span>
    : <button {...rest} type={type} {...state}
        className="inline-flex items-center gap-1 pl-2.5 pr-1 py-1 rounded-l-full min-w-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
        {body}
      </button>;

  return (
    <span className={`${chipClass({ selected, tone, dashed })} ${className}`}>
      {label}
      <Tooltip content={removeLabel ?? 'Снять'}>
        <button type="button" onClick={onRemove} aria-label={removeLabel ?? 'Снять'}
          disabled={rest.disabled}
          className="pl-0.5 pr-2 py-1 rounded-r-full hover:text-error focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
          <X size={12} aria-hidden />
        </button>
      </Tooltip>
    </span>
  );
}
