import { Check, X, type LucideIcon } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
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
 *
 * Чипы отбора над таблицей с правкой условия на месте — `shared/filter/FilterChips`: там чип — лицо
 * условия с поповером, а не переключатель.
 */
export interface ChipProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  children: ReactNode;
  selected?: boolean;
  icon?: LucideIcon;
  count?: ReactNode;
  tone?: 'neutral' | 'warning' | 'danger';
  dashed?: boolean;
  onRemove?: () => void;
  removeLabel?: string;
}

const BASE = 'inline-flex items-center gap-1 rounded-full border text-xs bg-surface transition-colors ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand disabled:opacity-50 disabled:pointer-events-none';

const TONE: Record<NonNullable<ChipProps['tone']>, string> = {
  neutral: 'border-stroke text-fg3 hover:text-fg1 hover:border-stroke-strong',
  warning: 'border-warning-border text-warning hover:bg-warning-subtle',
  danger: 'border-danger text-danger hover:bg-danger-subtle',
};

const SELECTED = 'border-brand text-brand bg-brand-subtle';

function chipClass({ selected = false, tone = 'neutral', dashed = false }:
  Pick<ChipProps, 'selected' | 'tone' | 'dashed'> = {}) {
  return `${BASE} ${selected ? SELECTED : `${TONE[tone]}${dashed ? ' border-dashed' : ''}`}`;
}

export function Chip({ children, selected, icon: Icon, count, tone, dashed, onRemove, removeLabel, className = '', type = 'button', ...rest }: ChipProps) {
  const toggles = selected !== undefined;
  const Shown = toggles && selected ? Check : Icon;
  const body = (
    <>
      {Shown && <Shown size={12} aria-hidden className="shrink-0" />}
      <span className="min-w-0 truncate">{children}</span>
      {count !== undefined && <span className="font-medium tabular-nums">· {count}</span>}
    </>
  );

  if (!onRemove)
    return (
      <button {...rest} type={type} aria-pressed={toggles ? selected : undefined}
        className={`${chipClass({ selected, tone, dashed })} px-2.5 py-1 ${className}`}>
        {body}
      </button>
    );

  return (
    <span className={`${chipClass({ selected, tone, dashed })} ${className}`}>
      <button {...rest} type={type} aria-pressed={toggles ? selected : undefined}
        className="inline-flex items-center gap-1 pl-2.5 pr-1 py-1 rounded-l-full min-w-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
        {body}
      </button>
      <Tooltip content={removeLabel ?? 'Снять'}>
        <button type="button" onClick={onRemove} aria-label={removeLabel ?? 'Снять'}
          disabled={rest.disabled}
          className="pl-0.5 pr-2 py-1 rounded-r-full hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
          <X size={12} aria-hidden />
        </button>
      </Tooltip>
    </span>
  );
}
