import type { MouseEvent, ReactNode, Ref } from 'react';
import { Tooltip } from './Tooltip';
import { Link } from 'react-router';
import { useGuardedNavClick } from './NavigationGuard';
import { ListRowBody } from './listRow';
import { listRowBox, present, type ListRowContent } from './listRowBox';


type Target =
  | { onClick: () => void; to?: never; passive?: never }
  | { to: string; onClick?: never; passive?: never }
  /** Строка без своей цели ([UI-84]): действия — соседи у краёв, сама строка не нажимается и слоя нажатия не
   *  показывает. Ветка явная, чтобы цель не пропадала молча там, где она нужна. */
  | { passive: true; onClick?: never; to?: never; hint?: never; selected?: never };

export type ListItemProps = Target & ListRowContent & {
  /** Интерактивный ведущий элемент (флажок участия, ручка перетаскивания) — сосед цели перед ней, а не внутри
   *  кнопки. */
  leadingAction?: ReactNode;
  /** Подсказка строки (`Tooltip` на цели): показывается и наведением, и фокусом с клавиатуры, читалке —
   *  описанием цели. */
  hint?: string;
  /** Действие строки (`IconButton`, `RowActionsMenu`) — сосед цели у замыкающего края, виден всегда. */
  action?: ReactNode;
  /** Карточка этой строки открыта: `secondary-container` и `aria-current`. */
  selected?: boolean;
  /** Строка отмечена флажком для действия над несколькими: та же заливка, но без `aria-current` — выбор несёт
   *  флажок, и отмеченных бывает несколько. */
  checked?: boolean;
  /** Уровень в дереве (0 — корень), 16 на уровень. */
  depth?: number;
  /** На цель строки (у `passive` — на тело), а не на контейнер: прокрутка к открытой строке и фокус — её дело. */
  ref?: Ref<HTMLElement>;
  /** На цель строки — её ищут живые прогоны (`invoice-row`). */
  'data-testid'?: string;
  className?: string;
};

/**
 * Строка списка list-detail — list item MD3 ([UI-84], sharedui-md3-18; вариант А макетов 14): прямоугольник
 * во всю ширину колонки без скругления, отступы 16, в одну строку — не ниже 48, с `supporting` — не ниже 64.
 * Отступы, высоты и роли текста — общие с вариантом выбора `ListOption` (`listRow.tsx`). Строки руками в
 * экранах считает правило храповика `list-row`.
 *
 * <p>Цель одна — `onClick` (кнопка) или `to` (ссылка) — и тянется на всю строку; `action` и `leadingAction` —
 * её соседи, а не вложенный интерактив. Слой состояния и кольцо фокуса — у строки (`state-layer` с добавкой `state-layer-row`):
 * наведение — по всей строке, нажатие и фокус — по цели. Строка `passive` цели не имеет и слоя не показывает.
 * Обрезанная подпись — полностью в нативной подсказке `title` (как у `DetailTitle`), и только когда она в самом
 * деле обрезана: обрезку меряют на наведении, а не при каждой отрисовке, — подсказка у неурезанной подписи
 * повторяла бы её же.</p>
 */
export function ListItem({ headline, title, supporting, icon, iconTone, leading, leadingAction, trailing, hint, action,
  selected = false, checked = false, depth = 0, ref, 'data-testid': testId, className = '', ...target }: ListItemProps) {
  const filled = selected || checked;
  const box = listRowBox({ form: 'list', twoLine: present(supporting), lead: !!leadingAction, tail: !!action, depth });
  const body = <ListRowBody form="list" filled={filled} headline={headline} title={title} supporting={supporting}
    trailing={trailing} {...(icon ? { icon, iconTone } : { leading })} />;
  const fill = filled ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface';
  const lead = leadingAction && <span data-li-side="" className="shrink-0 flex items-center pl-2">{leadingAction}</span>;
  const tail = action && <span data-li-side="" className="shrink-0 flex items-center pr-2">{action}</span>;
  if (target.passive) {
    return (
      <div className={`flex items-stretch w-full ${fill} ${className}`}>
        {lead}
        <div ref={ref as Ref<HTMLDivElement>} data-testid={testId} className={box}>{body}</div>
        {tail}
      </div>
    );
  }
  // Цель — кнопка или ссылка; ссылка на неё приходит общим типом, сужается здесь.
  const common = { ref: ref as Ref<HTMLButtonElement & HTMLAnchorElement>, className: `${box} text-left cursor-pointer focus:outline-none`,
    'data-li-target': '', 'data-testid': testId, 'aria-current': selected ? 'true' as const : undefined };
  const targetEl = target.to !== undefined
    ? <ListItemLink to={target.to} {...common}>{body}</ListItemLink>
    : <button type="button" onClick={target.onClick} {...common}>{body}</button>;
  return (
    <div className={`flex items-stretch w-full state-layer state-layer-row ${fill} ${className}`}>
      {lead}
      {hint ? <Tooltip content={hint}>{targetEl}</Tooltip> : targetEl}
      {tail}
    </div>
  );
}

/** Ссылка-цель под охраной ухода. Щелчок, пришедший сверху (подсказка `hint` вешает свой), охрану не заменяет. */
function ListItemLink({ to, children, onClick, ...rest }: { to: string; children: ReactNode; ref?: Ref<HTMLAnchorElement>; className: string;
  'data-li-target': string; 'data-testid'?: string; 'aria-current'?: 'true'; onClick?: (e: MouseEvent<HTMLAnchorElement>) => void }) {
  const guardedClick = useGuardedNavClick();
  return <Link to={to} {...rest} onClick={e => { onClick?.(e); guardedClick(to)(e); }}>{children}</Link>;
}
