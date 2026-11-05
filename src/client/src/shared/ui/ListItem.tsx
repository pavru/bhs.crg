import type { MouseEvent, ReactNode, Ref } from 'react';
import { Tooltip } from './Tooltip';
import { Link } from 'react-router';
import { Icon } from './Icon';
import type { IconDef } from './icons/types';
import { useGuardedNavClick } from './NavigationGuard';
import { fullTextIfCut } from './fullTextIfCut';
import { listDepthPad } from './listDepth';


type Target =
  | { onClick: () => void; to?: never }
  | { to: string; onClick?: never };

/** Ведущий слот — знак или узел, не оба: второй молча пропал бы. */
type Lead =
  | {
      /** Ведущий знак — 24 (Л5). */ icon?: IconDef; leading?: never;
      /** Цвет знака по смыслу строки (`text-primary`, `text-error`) — и на выбранной. Без него знак тихий: тон
       *  второй строки, на выбранной — `on-secondary-container`. */
      iconTone?: string;
    }
  | { /** Ведущий узел вместо знака (миниатюра листа) — внутри цели, неинтерактивный. */ leading?: ReactNode; icon?: never; iconTone?: never };

export type ListItemProps = Target & Lead & {
  /** Подпись — `body-large`, в одну строку с многоточием. */
  headline: ReactNode;
  /** Полный текст подписи для подсказки `title` при обрезке (Р6). По умолчанию — сама подпись, если она строка. */
  title?: string;
  /** Вторая строка — `body-medium` роли `on-surface-variant`. Строкой — в одну строчку с многоточием; узлом —
   *  как есть: многострочное содержимое (строка счёта) растит строку, а не режется. */
  supporting?: ReactNode;
  /** Интерактивный ведущий элемент (флажок участия) — сосед цели перед ней, а не внутри кнопки. */
  leadingAction?: ReactNode;
  /** Замыкающий текст, число (`CountText`), пометка (`StatusText`), шеврон — внутри цели. `false` и пустая
   *  строка — слота нет (`cond && …`): пустой слот с отступом отнимал бы 16 у подписи. */
  trailing?: ReactNode;
  /** Подсказка строки (`Tooltip` на цели): показывается и наведением, и фокусом с клавиатуры, читалке —
   *  описанием цели. */
  hint?: string;
  /** Действие строки (`IconButton`, `RowActionsMenu`) — сосед цели у замыкающего края, виден всегда. */
  action?: ReactNode;
  /** Карточка этой строки открыта: `secondary-container` и `aria-current`. */
  selected?: boolean;
  /** Уровень в дереве (0 — корень), 16 на уровень. */
  depth?: number;
  /** На цель строки, а не на контейнер: прокрутка к открытой строке и фокус — её дело. */
  ref?: Ref<HTMLElement>;
  /** На цель строки — её ищут живые прогоны (`invoice-row`). */
  'data-testid'?: string;
  className?: string;
};

/**
 * Строка списка list-detail — list item MD3 ([UI-84], sharedui-md3-18; вариант А макетов 14): прямоугольник
 * во всю ширину колонки без скругления, отступы 16, в одну строку — не ниже 48, с `supporting` — не ниже 64.
 * Строки руками в экранах считает правило храповика `list-row`.
 *
 * <p>Цель одна — `onClick` (кнопка) или `to` (ссылка) — и тянется на всю строку; `action` и `leadingAction` —
 * её соседи, а не вложенный интерактив. Слой состояния и кольцо фокуса — у строки (`state-layer` с добавкой `state-layer-row`):
 * наведение — по всей строке, нажатие и фокус — по цели. Обрезанная подпись — полностью в нативной
 * подсказке `title` (как у `DetailTitle`), и только когда она в самом деле обрезана: обрезку меряют на
 * наведении, а не при каждой отрисовке, — подсказка у неурезанной подписи повторяла бы её же.</p>
 */
export function ListItem({ headline, title, supporting, icon, iconTone, leading, leadingAction, trailing, hint, action,
  selected = false, depth = 0, ref, 'data-testid': testId, className = '', ...target }: ListItemProps) {
  const tone = selected ? 'text-on-secondary-container' : 'text-on-surface-variant';
  // Одно условие для слотов: 0 — содержимое, `false`, пустая строка и null — нет (`cond && …` у вызывающих).
  const twoLine = present(supporting);
  const full = title ?? (typeof headline === 'string' ? headline : undefined);
  const pad = `${leadingAction ? 'pl-2' : listDepthPad(depth)} ${action ? 'pr-2' : 'pr-4'}`;
  const cls = `flex-1 min-w-0 flex items-center gap-4 ${pad} py-2 ${twoLine ? 'min-h-16' : 'min-h-12'} text-left cursor-pointer focus:outline-none`;
  const body = (
    <>
      {icon ? <Icon icon={icon} size={24} className={`shrink-0 ${iconTone ?? tone}`} /> : leading}
      <span className="flex-1 min-w-0 flex flex-col">
        <span className="truncate text-body-large" onPointerEnter={full ? fullTextIfCut(full) : undefined}>
          {headline}
        </span>
        {twoLine && (
          <span className={`text-body-medium ${tone} ${typeof supporting === 'string' ? 'truncate' : ''}`}>{supporting}</span>
        )}
      </span>
      {present(trailing) && <span className={`shrink-0 flex items-center gap-2 text-label-small ${tone}`}>{trailing}</span>}
    </>
  );
  // Цель — кнопка или ссылка; ссылка на неё приходит общим типом, сужается здесь.
  const common = { ref: ref as Ref<HTMLButtonElement & HTMLAnchorElement>, className: cls, 'data-li-target': '', 'data-testid': testId, 'aria-current': selected ? 'true' as const : undefined };
  const targetEl = target.to !== undefined
    ? <ListItemLink to={target.to} {...common}>{body}</ListItemLink>
    : <button type="button" onClick={target.onClick} {...common}>{body}</button>;
  return (
    <div className={`flex items-stretch w-full state-layer state-layer-row ${
      selected ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'} ${className}`}>
      {leadingAction && <span data-li-side="" className="shrink-0 flex items-center pl-2">{leadingAction}</span>}
      {hint ? <Tooltip content={hint}>{targetEl}</Tooltip> : targetEl}
      {action && <span data-li-side="" className="shrink-0 flex items-center pr-2">{action}</span>}
    </div>
  );
}

const present = (node: ReactNode) => node != null && node !== false && node !== '';

/** Ссылка-цель под охраной ухода. Щелчок, пришедший сверху (подсказка `hint` вешает свой), охрану не заменяет. */
function ListItemLink({ to, children, onClick, ...rest }: { to: string; children: ReactNode; ref?: Ref<HTMLAnchorElement>; className: string;
  'data-li-target': string; 'data-testid'?: string; 'aria-current'?: 'true'; onClick?: (e: MouseEvent<HTMLAnchorElement>) => void }) {
  const guardedClick = useGuardedNavClick();
  return <Link to={to} {...rest} onClick={e => { onClick?.(e); guardedClick(to)(e); }}>{children}</Link>;
}
