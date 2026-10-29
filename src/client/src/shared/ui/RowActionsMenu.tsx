import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Icon } from './Icon';
import { ChevronRight, MoreVert } from './icons';
import type { ReactNode } from 'react';

export interface RowAction {
  key: string;
  label: string;
  icon?: ReactNode;
  onSelect?: () => void;
  disabled?: boolean;
  /** Правый бейдж — напр. количество активных условий фильтра. */
  badge?: string;
  /** Подсветить как активное (настройка задана). */
  active?: boolean;
  /** Опасное действие (удаление) — красный цвет + отделяется сепаратором. */
  danger?: boolean;
  /** Подменю (напр. список шаблонов для применения). */
  submenu?: RowAction[];
}

// Подсветка пункта — слоем on-surface ([UI-64]), мышью и стрелкой одинаково (data-highlighted Radix).
const itemCls =
  'flex items-center gap-2 px-3 py-1.5 text-body-medium text-on-surface cursor-pointer select-none outline-none ' +
  'state-layer data-[disabled]:pointer-events-none';

function Row({ action }: { action: RowAction }) {
  // Отключённое — содержимое on-surface 38 % ([UI-64]): у частей пункта свои цвета, поэтому — здесь.
  const off = action.disabled ? 'text-on-surface/38' : '';
  const color = off || (action.danger ? 'text-error' : action.active ? 'text-primary' : 'text-on-surface-variant');
  return (
    <>
      {action.icon && <span className={off || (action.active ? 'text-primary' : action.danger ? 'text-error' : 'text-on-surface-variant')}>{action.icon}</span>}
      <span className={`truncate ${color}`}>{action.label}</span>
      {action.badge && <span className={`ml-auto text-label-small tabular-nums ${off || 'text-on-surface-variant'}`}>{action.badge}</span>}
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
            className="z-50 min-w-[10rem] max-h-72 overflow-auto rounded-sm border border-outline-variant bg-surface py-1 shadow-elevation-2">
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
      <DropdownMenu.Trigger asChild>
        <button type="button" aria-label={ariaLabel} onClick={e => e.stopPropagation()}
          className="relative p-1 rounded-xs text-on-surface-variant state-layer focus-ring">
          <Icon icon={MoreVert} size={18} />
          {hasActive && <span className="absolute top-0.5 right-0.5 w-1.5 h-1.5 rounded-full bg-primary" />}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4} onClick={e => e.stopPropagation()}
          className="z-50 min-w-[11rem] rounded-sm border border-outline-variant bg-surface py-1 shadow-elevation-2">
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
