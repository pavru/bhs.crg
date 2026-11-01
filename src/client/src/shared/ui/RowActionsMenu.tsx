import { IconButton } from './Button';
import { CountText } from './CountText';
import type { IconDef } from './icons/types';
import { MoreVert } from './icons';
import { Menu, MenuItem, MenuNote, MenuSeparator, MenuSub } from './Menu';

export interface RowAction {
  key: string;
  label: string;
  /** Знак пункта — символ набора: размер и цвет ставит меню ([UI-76]). */
  icon?: IconDef;
  onSelect?: () => void;
  disabled?: boolean;
  /** Идёт запрос: вместо знака — кольцо хода. */
  loading?: boolean;
  /** Число справа (`CountText`) — напр. количество активных условий фильтра. */
  badge?: string;
  /** Опасное действие (удаление) — тона ошибки + отделяется чертой. */
  danger?: boolean;
  /** Строка под подписью — почему пункт погашен: подсказки при наведении на телефоне нет. */
  hint?: string;
  /** Подменю (напр. список шаблонов для применения). */
  submenu?: RowAction[];
}

function renderAction(action: RowAction) {
  if (action.submenu) {
    return (
      <MenuSub key={action.key} label={action.label} icon={action.icon} description={action.hint} disabled={action.disabled}>
        {action.submenu.length === 0
          ? <MenuNote>Нет вариантов</MenuNote>
          : action.submenu.map(renderAction)}
      </MenuSub>
    );
  }
  return (
    <MenuItem key={action.key} icon={action.icon} loading={action.loading} danger={action.danger}
      description={action.hint} disabled={action.disabled} onSelect={() => action.onSelect?.()}
      trailing={action.badge && <CountText disabled={action.disabled}>{action.badge}</CountText>}>
      {action.label}
    </MenuItem>
  );
}

/**
 * Меню inline-действий над записью («три точки»). Конвенция: когда у записи больше трёх действий,
 * сворачивать редкие/конфигурационные в это меню, оставляя видимыми 1-2 основных. Удаление — всегда
 * ЗДЕСЬ, отдельным пунктом (не hover-only красная иконка), и должно открывать ConfirmDialog, а не
 * удалять сразу (см. feedback_delete_ui_safety). Точка на триггере (<paramref="hasActive"/>)
 * сигналит, что внутри есть активная настройка. Слой, клавиатура и «Назад» — у общего `Menu`.
 */
export function RowActionsMenu({ actions, ariaLabel = 'Действия', hasActive, dense }: {
  actions: RowAction[]; ariaLabel?: string; hasActive?: boolean;
  /** Пункт 40 — меню в клетке таблицы и тулбаре. */
  dense?: boolean;
}) {
  const normal = actions.filter(a => !a.danger);
  const danger = actions.filter(a => a.danger);

  return (
    // Триггер — `IconButton` ([UI-46]): того же размера, что соседние кнопки-иконки клетки ([UI-68]).
    // Точка — поверх кнопки, а не в ней: знак кнопка рисует сама.
    <span className="relative inline-flex shrink-0">
      <Menu align="end" dense={dense} onClick={e => e.stopPropagation()}
        trigger={<IconButton label={ariaLabel} icon={MoreVert} onClick={e => e.stopPropagation()} />}>
        {normal.map(renderAction)}
        {danger.length > 0 && normal.length > 0 && <MenuSeparator />}
        {danger.map(renderAction)}
      </Menu>
      {hasActive && <span aria-hidden className="pointer-events-none absolute top-1 right-1 size-1.5 rounded-full bg-primary" />}
    </span>
  );
}
