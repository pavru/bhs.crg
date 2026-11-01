import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { IconButton } from './Button';
import { Icon } from './Icon';
import { ChevronRight, MoreVert } from './icons';
import type { ReactNode } from 'react';
import { CountText } from './CountText';
import { MENU_ITEM, MENU_ITEM_OFF, MENU_SURFACE } from './menuClasses';

export interface RowAction {
  key: string;
  label: string;
  icon?: ReactNode;
  onSelect?: () => void;
  disabled?: boolean;
  /** Число справа (`CountText`) — напр. количество активных условий фильтра. */
  badge?: string;
  /** Подсветить как активное (настройка задана). */
  active?: boolean;
  /** Опасное действие (удаление) — красный цвет + отделяется сепаратором. */
  danger?: boolean;
  /** Строка под подписью — почему пункт погашен: подсказки при наведении на телефоне нет. */
  hint?: string;
  /** Подменю (напр. список шаблонов для применения). */
  submenu?: RowAction[];
}

const itemCls = `flex items-center gap-2 px-3 py-1.5 text-body-medium text-on-surface ${MENU_ITEM}`;

function Row({ action }: { action: RowAction }) {
  const off = action.disabled ? MENU_ITEM_OFF : '';
  const color = off || (action.danger ? 'text-error' : action.active ? 'text-primary' : 'text-on-surface-variant');
  return (
    <>
      {action.icon && <span className={off || (action.active ? 'text-primary' : action.danger ? 'text-error' : 'text-on-surface-variant')}>{action.icon}</span>}
      {action.hint ? (
        <span className="flex flex-col min-w-0">
          <span className={`truncate ${color}`}>{action.label}</span>
          <span className="text-body-small text-on-surface-variant">{action.hint}</span>
        </span>
      ) : <span className={`truncate ${color}`}>{action.label}</span>}
      {action.badge && <CountText disabled={action.disabled} className="ml-auto">{action.badge}</CountText>}
    </>
  );
}

function renderAction(action: RowAction) {
  if (action.submenu) {
    return (
      <DropdownMenu.Sub key={action.key}>
        <DropdownMenu.SubTrigger className={itemCls} disabled={action.disabled}>
          <Row action={action} />
          <Icon icon={ChevronRight} size={18} className="ml-auto text-on-surface-variant" />
        </DropdownMenu.SubTrigger>
        <DropdownMenu.Portal>
          <DropdownMenu.SubContent
            className={`z-50 min-w-[10rem] max-h-72 overflow-auto ${MENU_SURFACE}`}>
            {action.submenu.length === 0
              ? <div className="px-3 py-1.5 text-body-small text-on-surface-variant">Нет вариантов</div>
              : action.submenu.map(sub => (
                <DropdownMenu.Item key={sub.key} className={itemCls} disabled={sub.disabled}
                  onSelect={() => sub.onSelect?.()}>
                  <Row action={sub} />
                </DropdownMenu.Item>
              ))}
          </DropdownMenu.SubContent>
        </DropdownMenu.Portal>
      </DropdownMenu.Sub>
    );
  }
  return (
    <DropdownMenu.Item key={action.key} className={itemCls} disabled={action.disabled}
      onSelect={() => action.onSelect?.()}>
      <Row action={action} />
    </DropdownMenu.Item>
  );
}

/**
 * Меню inline-действий над записью («три точки»). Конвенция: когда у записи больше трёх действий,
 * сворачивать редкие/конфигурационные в это меню, оставляя видимыми 1-2 основных. Удаление — всегда
 * ЗДЕСЬ, отдельным пунктом (не hover-only красная иконка), и должно открывать ConfirmDialog, а не
 * удалять сразу (см. feedback_delete_ui_safety). Точка на триггере (<paramref="hasActive"/>)
 * сигналит, что внутри есть активная настройка. Radix DropdownMenu — клавиатура/Esc/focus-trap из коробки.
 */
export function RowActionsMenu({ actions, ariaLabel = 'Действия', hasActive }: {
  actions: RowAction[]; ariaLabel?: string; hasActive?: boolean;
}) {
  const normal = actions.filter(a => !a.danger);
  const danger = actions.filter(a => a.danger);

  return (
    <DropdownMenu.Root>
      {/* Триггер — `IconButton` ([UI-46]): того же размера, что соседние кнопки-иконки клетки ([UI-68]).
          Точка — поверх кнопки, а не в ней: знак кнопка рисует сама. */}
      <span className="relative inline-flex shrink-0">
        <DropdownMenu.Trigger asChild>
          <IconButton label={ariaLabel} icon={MoreVert} onClick={e => e.stopPropagation()} />
        </DropdownMenu.Trigger>
        {hasActive && <span aria-hidden className="pointer-events-none absolute top-1 right-1 size-1.5 rounded-full bg-primary" />}
      </span>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4} onClick={e => e.stopPropagation()}
          className={`z-50 min-w-[11rem] ${MENU_SURFACE}`}>
          {normal.map(renderAction)}
          {danger.length > 0 && normal.length > 0 && (
            <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
          )}
          {danger.map(renderAction)}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
