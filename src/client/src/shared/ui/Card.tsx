import { Children, useId, type ReactNode } from 'react';
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
 * <p>Кликабельная карточка (`to` — ссылка, `onClick` — кнопка, не вместе) целиком одна цель: слой состояния под
 * курсором и возвышение на уровень выше. Цель — прозрачная кнопка или ссылка поверх всей карточки, а не обёртка
 * вокруг неё: заголовок остаётся заголовком для читалки, а имя цели — он же (`aria-labelledby`), потому `title`
 * у неё обязателен. Кольцо фокуса — у самой карточки. Других кнопок внутри нет — их накрывает цель; поэтому
 * у неё нет `menu` и `actions`.</p>
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

interface ClickableLook extends CardLook {
  /** Имя цели для читалки. */
  title: ReactNode;
  menu?: never;
  actions?: never;
}

/** Карточка — ссылка: переход под охраной ухода, как у ссылок навигации. */
interface LinkCard extends ClickableLook { to: string; onClick?: never }

interface ButtonCard extends ClickableLook { onClick: () => void; to?: never }

export type CardProps = StaticCard | LinkCard | ButtonCard;

export function Card(props: CardProps) {
  const { variant = 'outlined', title, icon, className = '', 'data-testid': testId, children } = props;
  const v = VARIANT[variant];
  const titleId = useId();
  const clickable = props.to !== undefined || props.onClick !== undefined;
  // Пустое тело (`null`, `false`, условие не сработало) отступа не рисует.
  const hasBody = Children.toArray(children).length > 0;
  const look = `relative flex flex-col overflow-hidden rounded-md text-left text-on-surface ${v.look} ${
    clickable ? `transition-shadow ${v.hover} has-[:focus-visible]:focus-ring-mark` : ''} ${className}`;
  // Цель кликабельной карточки — поверх всей карточки; фокус рисует карточка, слой нажатия — сама цель.
  const target = 'absolute inset-0 rounded-md state-layer cursor-pointer focus:outline-none';
  return (
    <div className={look} data-testid={testId}>
      {(title || icon) && (
        <div className="flex items-center gap-3 min-h-14 py-2 pr-2 pl-4">
          {icon && <Icon icon={icon} size={24} className="shrink-0 text-on-surface-variant" aria-hidden />}
          <h3 id={titleId} className="flex-1 min-w-0 text-title-medium text-on-surface">{title}</h3>
          {!clickable && props.menu}
        </div>
      )}
      {hasBody && (
        <div className={`flex flex-col gap-3 px-4 pb-4 text-body-medium text-on-surface-variant ${title || icon ? '' : 'pt-4'}`}>
          {children}
        </div>
      )}
      {!clickable && props.actions && <div className="flex justify-end gap-2 px-4 pb-4">{props.actions}</div>}
      {props.to !== undefined && <CardLink to={props.to} className={target} labelledBy={titleId} />}
      {props.onClick && <button type="button" onClick={props.onClick} className={target} aria-labelledby={titleId} />}
    </div>
  );
}

function CardLink({ to, className, labelledBy }: { to: string; className: string; labelledBy: string }) {
  const guardedClick = useGuardedNavClick();
  return <Link to={to} className={className} aria-labelledby={labelledBy} onClick={guardedClick(to)} />;
}
