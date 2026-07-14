import * as Dialog from '@radix-ui/react-dialog';
import { Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '@/shared/hooks/useAuth';
import { workNav, settingsNav } from './navConfig';

/**
 * Командная палитра (issue #107, Ctrl+K) — быстрый переход к любому разделу с клавиатуры.
 * v1: навигация. Стрелки ↑↓ двигают выделение, Enter — переход, Esc — закрыть, ввод фильтрует.
 * Открывается глобальным Ctrl/⌘+K (слушатель в AppShell). Только после закрытия per-widget
 * контрактов (F1–F3) — палитра ведёт в клавиатурно-проходимые экраны.
 */
export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const navigate = useNavigate();
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';

  const items = useMemo(() => [
    ...workNav.map(n => ({ ...n, section: 'Документы и данные' })),
    ...(isAdmin ? settingsNav.map(n => ({ ...n, section: 'Настройка системы' })) : []),
  ], [isAdmin]);

  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const filtered = useMemo(
    () => items.filter(i => i.label.toLowerCase().includes(query.trim().toLowerCase())),
    [items, query],
  );

  useEffect(() => { setActive(0); }, [query]);
  useEffect(() => { if (!open) setQuery(''); }, [open]);

  function go(to: string) { navigate(to); onOpenChange(false); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, filtered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const it = filtered[active]; if (it) go(it.to); }
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          className="fixed left-1/2 top-24 z-50 w-full max-w-lg -translate-x-1/2 overflow-hidden rounded-2xl border border-stroke bg-surface focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}
        >
          <Dialog.Title className="sr-only">Командная палитра</Dialog.Title>
          <div className="flex items-center gap-2 border-b border-stroke px-4">
            <Search size={16} className="shrink-0 text-fg4" />
            <input
              autoFocus value={query} onChange={e => setQuery(e.target.value)} onKeyDown={onKey}
              placeholder="Перейти к разделу…"
              className="h-12 flex-1 bg-transparent text-sm text-fg1 outline-none placeholder:text-fg4"
            />
            <kbd className="rounded border border-stroke px-1 text-[10px] text-fg4">Esc</kbd>
          </div>
          <ul className="max-h-80 overflow-y-auto p-2">
            {filtered.length === 0 ? (
              <li className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</li>
            ) : filtered.map((it, i) => (
              <li key={it.to}>
                <button
                  type="button" onMouseEnter={() => setActive(i)} onClick={() => go(it.to)}
                  className={`flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-sm transition-colors ${
                    i === active ? 'bg-tonal text-on-tonal' : 'text-fg1'}`}
                >
                  <it.icon size={16} className={i === active ? 'text-on-tonal' : 'text-fg3'} />
                  <span className="flex-1">{it.label}</span>
                  <span className="text-[10px] text-fg4">{it.section}</span>
                </button>
              </li>
            ))}
          </ul>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
