import { useState, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { KeyboardArrowDown } from '@/shared/ui/icons';

/**
 * Сворачиваемая группа настроек: шапка-кнопка с шевроном + тело.
 * Состояние сворачивания сохраняется в localStorage по storageKey.
 */
export function CollapsibleSection({
  title, children, defaultOpen = true, storageKey, right,
}: {
  title: string;
  children: ReactNode;
  defaultOpen?: boolean;
  storageKey?: string;
  right?: ReactNode;
}) {
  const lsKey = storageKey ? `crg.section.${storageKey}` : null;
  const [open, setOpen] = useState(() => {
    if (lsKey) {
      const v = localStorage.getItem(lsKey);
      if (v !== null) return v === '1';
    }
    return defaultOpen;
  });

  function toggle() {
    setOpen(o => {
      const next = !o;
      if (lsKey) localStorage.setItem(lsKey, next ? '1' : '0');
      return next;
    });
  }

  return (
    <div className="border border-outline-variant rounded-lg overflow-hidden">
      <button
        type="button"
        onClick={toggle}
        className="w-full flex items-center justify-between gap-2 p-4 text-left state-layer"
        aria-expanded={open}
      >
        <h2 className="text-body-medium font-semibold text-on-surface-variant uppercase tracking-wide">{title}</h2>
        <span className="flex items-center gap-2">
          {right}
          <Icon icon={KeyboardArrowDown} size={18} className={`text-on-surface-variant shrink-0 transition-transform ${open ? '' : '-rotate-90'}`} />
        </span>
      </button>
      {open && <div className="px-4 pb-4 space-y-4">{children}</div>}
    </div>
  );
}
