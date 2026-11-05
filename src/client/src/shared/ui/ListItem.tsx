import type { ReactNode, Ref } from 'react';
import { Link } from 'react-router';
import { Icon } from './Icon';
import type { IconDef } from './icons/types';
import { useGuardedNavClick } from './NavigationGuard';

/** Отступ по глубине дерева — 16 на уровень (D8, `UserLibFileList`). Классом целиком: Tailwind не соберёт
 *  имя, склеенное из частей. */
const DEPTH_PAD = ['pl-4', 'pl-8', 'pl-12', 'pl-16', 'pl-20', 'pl-24'];

type Target =
  | { onClick: () => void; to?: never }
  | { to: string; onClick?: never };

export type ListItemProps = Target & {
  /** Подпись — `body-large`, в одну строку с многоточием. */
  headline: ReactNode;
  /** Полный текст подписи для подсказки `title` при обрезке (Р6). По умолчанию — сама подпись, если она строка. */
  title?: string;
  /** Вторая строка — `body-medium` роли `on-surface-variant`. Строкой — в одну строчку с многоточием; узлом —
   *  как есть: многострочное содержимое (строка счёта) растит строку, а не режется. */
  supporting?: ReactNode;
  /** Ведущий знак — 24 (Л5). */
  icon?: IconDef;
  /** Ведущий узел вместо знака (миниатюра листа) — внутри цели, неинтерактивный. */
  leading?: ReactNode;
  /** Интерактивный ведущий элемент (флажок участия) — сосед цели перед ней, а не внутри кнопки. */
  leadingAction?: ReactNode;
  /** Замыкающий текст, число (`CountText`), пометка (`StatusText`), шеврон — внутри цели. */
  trailing?: ReactNode;
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
 * её соседи, а не вложенный интерактив. Слой состояния и кольцо фокуса — у строки (`state-layer-row`):
 * наведение — по всей строке, нажатие и фокус — по цели. Обрезанная подпись — полностью в нативной
 * подсказке `title` (исключение правила `native-title`, как у `DetailTitle`).</p>
 */
export function ListItem({ headline, title, supporting, icon, leading, leadingAction, trailing, action, selected = false,
  depth = 0, ref, 'data-testid': testId, className = '', ...target }: ListItemProps) {
  const tone = selected ? 'text-on-secondary-container' : 'text-on-surface-variant';
  const pad = `${leadingAction ? 'pl-2' : DEPTH_PAD[Math.min(depth, DEPTH_PAD.length - 1)]} ${action ? 'pr-2' : 'pr-4'}`;
  const cls = `flex-1 min-w-0 flex items-center gap-4 ${pad} py-2 ${supporting ? 'min-h-16' : 'min-h-12'} text-left cursor-pointer focus:outline-none`;
  const body = (
    <>
      {icon ? <Icon icon={icon} size={24} className={`shrink-0 ${tone}`} /> : leading}
      <span className="flex-1 min-w-0 flex flex-col">
        <span className="truncate text-body-large" title={title ?? (typeof headline === 'string' ? headline : undefined)}>
          {headline}
        </span>
        {supporting != null && (
          <span className={`text-body-medium ${tone} ${typeof supporting === 'string' ? 'truncate' : ''}`}>{supporting}</span>
        )}
      </span>
      {trailing != null && <span className={`shrink-0 flex items-center gap-2 text-label-small ${tone}`}>{trailing}</span>}
    </>
  );
  // Цель — кнопка или ссылка; ссылка на неё приходит общим типом, сужается здесь.
  const common = { ref: ref as Ref<HTMLButtonElement & HTMLAnchorElement>, className: cls, 'data-li-target': '', 'data-testid': testId, 'aria-current': selected ? 'true' as const : undefined };
  return (
    <div className={`flex items-stretch w-full state-layer-row ${
      selected ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'} ${className}`}>
      {leadingAction && <span className="shrink-0 flex items-center pl-2">{leadingAction}</span>}
      {target.to !== undefined
        ? <ListItemLink to={target.to} {...common}>{body}</ListItemLink>
        : <button type="button" onClick={target.onClick} {...common}>{body}</button>}
      {action && <span className="shrink-0 flex items-center pr-2">{action}</span>}
    </div>
  );
}

function ListItemLink({ to, children, ...rest }: { to: string; children: ReactNode; ref?: Ref<HTMLAnchorElement>; className: string;
  'data-li-target': string; 'data-testid'?: string; 'aria-current'?: 'true' }) {
  const guardedClick = useGuardedNavClick();
  return <Link to={to} onClick={guardedClick(to)} {...rest}>{children}</Link>;
}
