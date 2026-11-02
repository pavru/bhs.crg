import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { Icon } from './Icon';
import type { IconDef } from './icons/types';
import { useGuardedNavClick } from './NavigationGuard';

/**
 * Карточка MD3 ([UI-77], sharedui-md3-13): разметка без логики — вид, шапка, тело и действия. Карточки руками
 * в `features/` считает правило храповика `hand-card`; экраны переводятся в `sharedui-md3-17-screens`.
 *
 * <p>Три вида: outlined — `surface` в обводке `outline-variant`, filled — `surface-container-highest` без
 * обводки, elevated — `surface-container-low` с возвышением 1; радиус у всех 12. Шапка — не ниже 56,
 * заголовок `title-medium`, знак 24 у ведущего края, меню «⋮» — у замыкающего.</p>
 *
 * <p>Кликабельная карточка (`to` — ссылка, `onClick` — кнопка) целиком одна цель: слой состояния под
 * курсором и возвышение на уровень выше. Внутри неё других кнопок нет — вложенная цель в ссылке не
 * читается и не нажимается отдельно; поэтому у неё нет `menu` и `actions`.</p>
 */

export type CardVariant = 'outlined' | 'filled' | 'elevated';

const VARIANT: Record<CardVariant, { look: string; hover: string }> = {
  outlined: { look: 'bg-surface border border-outline-variant', hover: 'hover:shadow-elevation-1' },
  filled: { look: 'bg-surface-container-highest', hover: 'hover:shadow-elevation-1' },
  elevated: { look: 'bg-surface-container-low shadow-elevation-1', hover: 'hover:shadow-elevation-2' },
};

interface CardLook {
  variant?: CardVariant;
  /** Заголовок шапки; без него и без `icon` шапки нет. */
  title?: ReactNode;
  /** Знак 24 у ведущего края шапки. */
  icon?: IconDef;
  className?: string;
  'data-testid'?: string;
  children?: ReactNode;
}

interface StaticCard extends CardLook {
  /** У замыкающего края шапки — меню «⋮» карточки (`Menu`). */
  menu?: ReactNode;
  /** Ряд действий под телом, у замыкающего края: text и tonal, filled — одна. */
  actions?: ReactNode;
  to?: never;
  onClick?: never;
}

interface ClickableCard extends CardLook {
  menu?: never;
  actions?: never;
  /** Карточка — ссылка: переход под охраной ухода, как у ссылок навигации. */
  to?: string;
  onClick?: () => void;
}

export type CardProps = StaticCard | ClickableCard;

export function Card(props: CardProps) {
  const { variant = 'outlined', title, icon, className = '', 'data-testid': testId, children } = props;
  const v = VARIANT[variant];
  const clickable = props.to !== undefined || props.onClick !== undefined;
  const look = `relative flex flex-col overflow-hidden rounded-md text-left text-on-surface ${v.look} ${
    clickable ? `state-layer cursor-pointer transition-shadow ${v.hover}` : ''} ${className}`;
  const inner = (
    <>
      {(title || icon) && (
        <div className="flex items-center gap-3 min-h-14 py-2 pr-2 pl-4">
          {icon && <Icon icon={icon} size={24} className="shrink-0 text-on-surface-variant" aria-hidden />}
          <h3 className="flex-1 min-w-0 text-title-medium text-on-surface">{title}</h3>
          {!clickable && props.menu}
        </div>
      )}
      {children !== undefined && (
        <div className={`flex flex-col gap-3 px-4 pb-4 text-body-medium text-on-surface-variant ${title || icon ? '' : 'pt-4'}`}>
          {children}
        </div>
      )}
      {!clickable && props.actions && <div className="flex justify-end gap-2 px-4 pb-4">{props.actions}</div>}
    </>
  );
  if (props.to !== undefined) return <CardLink to={props.to} onClick={props.onClick} className={look} testId={testId}>{inner}</CardLink>;
  if (props.onClick) return <button type="button" onClick={props.onClick} className={look} data-testid={testId}>{inner}</button>;
  return <div className={look} data-testid={testId}>{inner}</div>;
}

function CardLink({ to, onClick, className, testId, children }: {
  to: string; onClick?: () => void; className: string; testId?: string; children: ReactNode;
}) {
  const guardedClick = useGuardedNavClick();
  return (
    <Link to={to} className={className} data-testid={testId}
      onClick={e => { onClick?.(); guardedClick(to)(e); }}>
      {children}
    </Link>
  );
}
