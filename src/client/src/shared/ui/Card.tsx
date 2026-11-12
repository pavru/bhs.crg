import { Children, useId, type HTMLAttributes, type ReactNode } from 'react';
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
 *
 * <p>Состояния — только у обычной карточки ([UI-81], sharedui-md3-17): `dragged` — карточку тянут (слой 16 %
 * `state-layer` по `data-dragging`, как у строк `DataGrid`, и уровень выше), `dropTarget` — над ней тянут
 * другую (обводка 2 px `primary`), `invalid` — в ней ошибка (обводка `error`). События перетаскивания
 * (`draggable`, `onDragStart`…`onDrop`) карточка отдаёт своему корню. `flush` — тело без отступов: строки,
 * которые экран рисует сам до краёв (шапка поля в конструкторе типа с ручкой и сводкой), в `title` не ложатся.
 * Кликабельной всё это не положено: цель накрывает карточку целиком, тянуть и бросать нечего.</p>
 *
 * <p>Знак шапки говорит о состоянии так же, как у `Chip` (shell-08): `iconTone` красит его ролью (`error`,
 * `warning`), `iconLabel` называет его читалке — ошибку от сомнения отличает знак и его имя, а не цвет. У
 * кликабельной карточки тело — описание цели (`aria-describedby`): иначе читалка назвала бы только заголовок,
 * а число очереди и её модуль остались бы немыми.</p>
 */

export type CardVariant = 'outlined' | 'filled' | 'elevated';

const TONE = { error: 'text-error', warning: 'text-warning' } as const;

const VARIANT: Record<CardVariant, { bg: string; edge: string; shadow: string; hover: string; dragged: string }> = {
  outlined: { bg: 'bg-surface', edge: 'border-outline-variant', shadow: '', hover: 'hover:shadow-elevation-1', dragged: 'shadow-elevation-3' },
  filled: { bg: 'bg-surface-container-highest', edge: '', shadow: '', hover: 'hover:shadow-elevation-1', dragged: 'shadow-elevation-3' },
  elevated: { bg: 'bg-surface-container-low', edge: '', shadow: 'shadow-elevation-1', hover: 'hover:shadow-elevation-2', dragged: 'shadow-elevation-4' },
};

type DragEvents = Pick<HTMLAttributes<HTMLDivElement>,
  'draggable' | 'onDragStart' | 'onDragEnd' | 'onDragEnter' | 'onDragOver' | 'onDragLeave' | 'onDrop'>;

/** Состояния и события обычной карточки; кликабельной — ни одного. */
interface CardStates extends DragEvents {
  /** Карточку тянут. */
  dragged?: boolean;
  /** Над карточкой тянут другую — сюда бросят. */
  dropTarget?: boolean;
  /** В карточке ошибка — обводка `error`. */
  invalid?: boolean;
  /** Тело без отступов и зазоров. */
  flush?: boolean;
}

type NoStates = { [K in keyof CardStates]?: never };

/** Знак шапки: без знака нет ни тона, ни имени — цвет без знака снова стал бы тоном карточки. */
type CardIcon =
  | { icon?: undefined; iconTone?: never; iconLabel?: never }
  | {
    /** Знак 24 у ведущего края шапки. */
    icon: IconDef;
    /** Тон знака — состояние, о котором карточка. Контейнер карточка не красит. */
    iconTone?: 'error' | 'warning';
    /** Имя знака для читалки; без него знак — украшение. */
    iconLabel?: string;
  };

type CardLook = CardIcon & {
  variant?: CardVariant;
  /** Заголовок шапки; без него и без `icon` шапки нет. */
  title?: ReactNode;
  className?: string;
  'data-testid'?: string;
  children?: ReactNode;
};

type StaticCard = CardLook & CardStates & {
  /** У замыкающего края шапки — меню «⋮» карточки (`Menu`). */
  menu?: ReactNode;
  /** Ряд действий под телом, у замыкающего края: text и tonal, filled — одна. */
  actions?: ReactNode;
  to?: never;
  onClick?: never;
};

type ClickableLook = CardLook & NoStates & {
  /** Имя цели для читалки. */
  title: ReactNode;
  menu?: never;
  actions?: never;
};

/** Карточка — ссылка: переход под охраной ухода, как у ссылок навигации. */
type LinkCard = ClickableLook & { to: string; onClick?: never };

type ButtonCard = ClickableLook & { onClick: () => void; to?: never };

export type CardProps = StaticCard | LinkCard | ButtonCard;

export function Card(props: CardProps) {
  // Что осталось после разбора — события перетаскивания (`DragEvents`): корень получает их одним списком.
  const { variant = 'outlined', title, icon, iconTone, iconLabel, className = '', 'data-testid': testId, children,
    dragged, dropTarget, invalid, flush, menu, actions, to, onClick, ...drag } = props;
  const v = VARIANT[variant];
  const titleId = useId();
  const bodyId = useId();
  const clickable = to !== undefined || onClick !== undefined;
  // Пустое тело (`null`, `false`, условие не сработало) отступа не рисует.
  const hasBody = Children.toArray(children).length > 0;
  // Ошибка — обводкой у любого вида: у filled и elevated своей нет, и без неё ошибку сказал бы только текст.
  const edge = invalid ? 'border border-error' : v.edge ? `border ${v.edge}` : '';
  const look = `relative flex flex-col overflow-hidden rounded-md text-left text-on-surface ${v.bg} ${edge} ${
    dragged ? `state-layer ${v.dragged}` : v.shadow} ${dropTarget ? 'outline-2 outline-primary -outline-offset-2' : ''} ${
    clickable ? `transition-shadow ${v.hover} has-[:focus-visible]:focus-ring-mark` : ''} ${className}`;
  // Цель кликабельной карточки — поверх всей карточки; фокус рисует карточка, слой нажатия — сама цель.
  const target = 'absolute inset-0 rounded-md state-layer cursor-pointer focus:outline-none';
  const head = !!(title || icon);
  const body = flush ? 'flex flex-col' : `flex flex-col gap-3 px-4 pb-4 text-body-medium text-on-surface-variant ${head ? '' : 'pt-4'}`;
  return (
    <div className={look} data-testid={testId} data-dragging={dragged || undefined} {...drag}>
      {head && (
        <div className="flex items-center gap-3 min-h-14 py-2 pr-2 pl-4">
          {icon && <Icon icon={icon} size={24} className={`shrink-0 ${iconTone ? TONE[iconTone] : 'text-on-surface-variant'}`}
            {...(iconLabel ? { 'aria-label': iconLabel } : { 'aria-hidden': true })} />}
          <h3 id={titleId} className="flex-1 min-w-0 text-title-medium text-on-surface">{title}</h3>
          {!clickable && menu}
        </div>
      )}
      {hasBody && <div id={bodyId} className={body}>{children}</div>}
      {!clickable && actions && <div className="flex justify-end gap-2 px-4 pb-4">{actions}</div>}
      {to !== undefined && <CardLink to={to} className={target} labelledBy={titleId} describedBy={hasBody ? bodyId : undefined} />}
      {onClick && <button type="button" onClick={onClick} className={target} aria-labelledby={titleId}
        aria-describedby={hasBody ? bodyId : undefined} />}
    </div>
  );
}

function CardLink({ to, className, labelledBy, describedBy }: {
  to: string; className: string; labelledBy: string; describedBy?: string;
}) {
  const guardedClick = useGuardedNavClick();
  return <Link to={to} className={className} aria-labelledby={labelledBy} aria-describedby={describedBy}
    onClick={guardedClick(to)} />;
}
