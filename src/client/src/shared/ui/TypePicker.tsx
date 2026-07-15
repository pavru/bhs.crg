import * as Dialog from '@radix-ui/react-dialog';
import { Search, Boxes, FileText } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';

/**
 * Searchable-пикер типа (issue: длинный несортированный Select) — модалка в стиле командной
 * палитры: поиск по имени+коду, блок «Недавние» (localStorage), сортировка по алфавиту,
 * клавиатура ↑↓/Enter/Esc, MD3-строки с ведущей иконкой семейства. Код показывается только
 * если отличается от имени (убираем шум «Название (Код)»).
 */
export interface PickType { id: string; name: string; code: string; section: string }

const RECENTS_CAP = 6;

export function TypePicker({ open, onOpenChange, types, onSelect, recentKey, title = 'Выберите тип' }: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  types: PickType[];
  onSelect: (id: string) => void;
  recentKey?: string;
  title?: string;
}) {
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);

  const storeKey = recentKey ? `typepicker-recents:${recentKey}` : null;
  function loadRecents(): string[] {
    if (!storeKey) return [];
    try { return JSON.parse(localStorage.getItem(storeKey) || '[]') as string[]; } catch { return []; }
  }
  function pushRecent(id: string) {
    if (!storeKey) return;
    const next = [id, ...loadRecents().filter(x => x !== id)].slice(0, RECENTS_CAP);
    localStorage.setItem(storeKey, JSON.stringify(next));
  }

  const q = query.trim().toLowerCase();
  const sections = useMemo(() => {
    const byName = (a: PickType, b: PickType) => a.name.localeCompare(b.name, 'ru');
    const matches = (t: PickType) => `${t.name} ${t.code}`.toLowerCase().includes(q);
    const groups: { label: string; items: PickType[] }[] = [];

    if (!q) {
      const recents = loadRecents()
        .map(id => types.find(t => t.id === id))
        .filter((t): t is PickType => !!t);
      if (recents.length) groups.push({ label: 'Недавние', items: recents });
    }
    const order: string[] = [];
    const bySection = new Map<string, PickType[]>();
    for (const t of types) {
      if (q && !matches(t)) continue;
      if (!bySection.has(t.section)) { bySection.set(t.section, []); order.push(t.section); }
      bySection.get(t.section)!.push(t);
    }
    for (const s of order) groups.push({ label: s, items: bySection.get(s)!.slice().sort(byName) });
    return groups;
  }, [types, q]); // eslint-disable-line react-hooks/exhaustive-deps

  const flat = useMemo(() => sections.flatMap(g => g.items), [sections]);

  useEffect(() => { setActive(0); }, [query]);
  useEffect(() => { if (!open) setQuery(''); }, [open]);

  function choose(t: PickType) { pushRecent(t.id); onSelect(t.id); onOpenChange(false); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, flat.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const t = flat[active]; if (t) choose(t); }
  }

  let idx = -1; // сквозной индекс строки (совпадает с порядком flat), корректен при дублях «Недавние»
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          className="fixed left-1/2 top-16 z-50 w-[calc(100vw-2rem)] max-w-lg -translate-x-1/2 flex flex-col max-h-[calc(100vh-8rem)] overflow-hidden rounded-2xl border border-stroke bg-surface focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}
        >
          <Dialog.Title className="sr-only">{title}</Dialog.Title>
          <div className="flex items-center gap-2 border-b border-stroke px-4 shrink-0">
            <Search size={16} className="shrink-0 text-fg4" />
            <input autoFocus value={query} onChange={e => setQuery(e.target.value)} onKeyDown={onKey}
              placeholder="Поиск типа по имени или коду…" aria-label="Поиск типа"
              className="h-12 flex-1 bg-transparent text-sm text-fg1 outline-none placeholder:text-fg4" />
            <kbd className="rounded border border-stroke px-1 text-[10px] text-fg4">Esc</kbd>
          </div>
          <ul className="overflow-y-auto p-2" role="listbox" aria-label={title}>
            {flat.length === 0 ? (
              <li className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</li>
            ) : sections.map(g => (
              <li key={g.label}>
                <div className="px-3 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wide text-fg4">{g.label}</div>
                <ul>
                  {g.items.map(t => {
                    const i = ++idx;
                    const Icon = t.section.toLowerCase().includes('документ') ? FileText : Boxes;
                    const code = t.code.trim();
                    const showCode = !!code && code.toLowerCase() !== t.name.trim().toLowerCase();
                    return (
                      <li key={`${g.label}:${t.id}`} role="option" aria-selected={i === active}>
                        <button type="button" onMouseEnter={() => setActive(i)} onClick={() => choose(t)}
                          className={`flex w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-sm transition-colors ${
                            i === active ? 'bg-tonal text-on-tonal' : 'text-fg1 hover:bg-black/5 dark:hover:bg-white/10'}`}>
                          <Icon size={16} className={i === active ? 'text-on-tonal' : 'text-fg3'} />
                          <span className="flex-1 truncate">{t.name}</span>
                          {showCode && <span className={`text-[11px] font-mono shrink-0 ${i === active ? 'text-on-tonal/80' : 'text-fg4'}`}>{code}</span>}
                        </button>
                      </li>
                    );
                  })}
                </ul>
              </li>
            ))}
          </ul>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
