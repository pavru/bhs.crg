import { useState, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';

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
    <div className="border border-stroke rounded-xl overflow-hidden">
      <button
        type="button"
        onClick={toggle}
        className="w-full flex items-center justify-between gap-2 p-4 text-left hover:bg-base transition-colors"
        aria-expanded={open}
      >
        <h2 className="text-sm font-semibold text-fg2 uppercase tracking-wide">{title}</h2>
        <span className="flex items-center gap-2">
          {right}
          <ChevronDown className={`w-4 h-4 text-fg3 shrink-0 transition-transform ${open ? '' : '-rotate-90'}`} />
        </span>
      </button>
      {open && <div className="px-4 pb-4 space-y-4">{children}</div>}
    </div>
  );
}
