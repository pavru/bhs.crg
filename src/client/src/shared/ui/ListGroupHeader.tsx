import type { ReactNode } from 'react';
import { Icon } from './Icon';
import { KeyboardArrowDown, KeyboardArrowUp } from './icons';
import { fullTextIfCut } from './fullTextIfCut';

/**
 * Заголовок сворачиваемой группы строк списка ([UI-84], sharedui-md3-18, вариант Б макетов 214e220b): подзаголовок
 * MD3 — `title-small` роли `on-surface-variant` — целью не ниже 48 с шевроном раскрытия у замыкающего края и
 * `aria-expanded`. Группа типов документов, шаблон с версиями в редакторе шаблонов.
 *
 * <p>Выбранным не бывает: заливки выбора и `aria-current` у него нет — выбрана бывает только строка внутри.
 * Строки группы идут без отступа. Действия группы («⋮») — `action`, сосед цели, видны всегда.</p>
 */
export function ListGroupHeader({ label, title, count, lead, open, onToggle, action }: {
  label: ReactNode;
  /** Полный текст обрезанной подписи — нативной подсказкой при обрезке, как у `ListItem`. */
  title?: string;
  /** Число строк — перед шевроном (`CountText` или текст). */
  count?: ReactNode;
  /** Пометка перед подписью (звезда шаблона по умолчанию). */
  lead?: ReactNode;
  open: boolean;
  onToggle: () => void;
  action?: ReactNode;
}) {
  const full = title ?? (typeof label === 'string' ? label : undefined);
  return (
    <div className="flex items-stretch w-full state-layer state-layer-row text-on-surface-variant">
      <button type="button" onClick={onToggle} aria-expanded={open} data-li-target=""
        className={`flex-1 min-w-0 flex items-center gap-2 pl-4 ${action ? 'pr-2' : 'pr-4'} pt-3 pb-1 min-h-12 text-left text-title-small cursor-pointer focus:outline-none`}>
        {lead}
        <span className="flex-1 min-w-0 truncate" onPointerEnter={full ? fullTextIfCut(full) : undefined}>{label}</span>
        {count != null && <span className="shrink-0">{count}</span>}
        <Icon icon={open ? KeyboardArrowUp : KeyboardArrowDown} size={24} className="shrink-0" />
      </button>
      {action && <span data-li-side="" className="shrink-0 flex items-end pr-2">{action}</span>}
    </div>
  );
}
