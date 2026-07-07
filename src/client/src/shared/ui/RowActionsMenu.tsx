import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { MoreVertical, ChevronRight } from 'lucide-react';
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

const itemCls =
  'flex items-center gap-2 px-3 py-1.5 text-sm cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-base data-[disabled]:opacity-40 data-[disabled]:pointer-events-none';

function Row({ action }: { action: RowAction }) {
  const color = action.danger ? 'text-danger' : action.active ? 'text-brand' : 'text-fg2';
  return (
    <>
      {action.icon && <span className={action.active ? 'text-brand' : action.danger ? 'text-danger' : 'text-fg4'}>{action.icon}</span>}
      <span className={`truncate ${color}`}>{action.label}</span>
      {action.badge && <span className="ml-auto text-[11px] text-fg4 tabular-nums">{action.badge}</span>}
    </>
  );
}

function renderAction(action: RowAction) {
  if (action.submenu) {
    return (
      <DropdownMenu.Sub key={action.key}>
        <DropdownMenu.SubTrigger className={itemCls} disabled={action.disabled}>
          <Row action={action} />
          <ChevronRight size={13} className="ml-auto text-fg4" />
        </DropdownMenu.SubTrigger>
        <DropdownMenu.Portal>
          <DropdownMenu.SubContent
            className="z-50 min-w-[10rem] max-h-72 overflow-auto rounded-md border border-stroke bg-surface py-1"
            style={{ boxShadow: 'var(--f-shadow28)' }}>
            {action.submenu.length === 0
              ? <div className="px-3 py-1.5 text-xs text-fg4">Нет вариантов</div>
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
          className="relative p-1 rounded text-fg4 hover:text-fg1 hover:bg-base outline-none focus-visible:ring-2 focus-visible:ring-brand/40">
          <MoreVertical size={14} />
          {hasActive && <span className="absolute top-0.5 right-0.5 w-1.5 h-1.5 rounded-full bg-brand" />}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4} onClick={e => e.stopPropagation()}
          className="z-50 min-w-[11rem] rounded-md border border-stroke bg-surface py-1"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          {normal.map(renderAction)}
          {danger.length > 0 && normal.length > 0 && (
            <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
          )}
          {danger.map(renderAction)}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
