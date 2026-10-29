import * as Dialog from '@radix-ui/react-dialog';
import { Icon } from './Icon';
import { Block, Search } from './icons';
import { typeIcon } from './typeIcons';
import { useMemo, useRef, useState } from 'react';
import { Tooltip } from './Tooltip';
import { useHistoryLayer } from './historyLayers';

/**
 * Searchable-пикер типа (issue: длинный несортированный Select) — модалка в стиле командной
 * палитры: поиск по имени+коду, блок «Недавние» (localStorage), сортировка по алфавиту,
 * клавиатура ↑↓/Enter/Esc, MD3-строки с ведущей иконкой семейства. Код показывается только
 * если отличается от имени (убираем шум «Название (Код)»).
 */
export interface PickType {
  id: string; name: string; code: string; section: string;
  /**
   * Пункт «в стороне» (issue #1219: комплект неизбранной стройки). Без запроса не показывается, пока
   * человек не нажал выход `aside.reveal`; поиск находит его всегда — отдельным блоком под заголовком
   * `aside.label`, после остальных групп.
   */
  aside?: boolean;
}

const RECENTS_CAP = 6;

interface TypePickerProps {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  types: PickType[];
  onSelect: (id: string) => void;
  recentKey?: string;
  title?: string;
  /** Строка «нет значения» первой в списке (для очищаемых пикеров — напр. «Без родителя»). */
  noneOption?: { label: string };
  onSelectNone?: () => void;
  /** Тексты для пунктов «в стороне»: заголовок их блока и выход внизу списка. */
  aside?: { label: string; reveal: string };
}

/**
 * Тело монтируется по открытию (issue #858): запрос и выделение заводятся заново, и эффект
 * «закрылось — очисти запрос» не нужен. Заодно закрытый пикер перестаёт считать группы и недавние.
 */
export function TypePicker(props: TypePickerProps) {
  return props.open ? <TypePickerBody {...props} /> : null;
}

function TypePickerBody({ onOpenChange, types, onSelect, recentKey, title = 'Выберите тип', noneOption, onSelectNone, aside }: TypePickerProps) {
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const [revealed, setRevealed] = useState(false);
  const input = useRef<HTMLInputElement>(null);

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
  // Пункты «в стороне» видны после выхода или по запросу: поиск — уже высказанное намерение. Без
  // текстов `aside` признак ничего не значит: спрятать пункт, не дав к нему выхода, — потерять его.
  const sideOf = (t: PickType) => !!aside && !!t.aside;
  const asideShown = revealed || !!q;
  const asideHidden = !asideShown && types.some(sideOf);
  const sections = useMemo(() => {
    const byName = (a: PickType, b: PickType) => a.name.localeCompare(b.name, 'ru');
    const matches = (t: PickType) => `${t.name} ${t.code}`.toLowerCase().includes(q);
    // Ключ — со стороной: одноимённые стройки бывают по обе стороны, и одна подпись дала бы два узла
    // с одним ключом (ревью PR #1332).
    const groups: { key: string; label: string; items: PickType[]; heading?: string }[] = [];

    if (!q) {
      const recents = loadRecents()
        .map(id => types.find(t => t.id === id))
        .filter((t): t is PickType => !!t && (asideShown || !sideOf(t)));
      if (recents.length) groups.push({ key: 'recent', label: 'Недавние', items: recents });
    }
    for (const side of [false, true]) {
      if (side && !asideShown) break;
      const order: string[] = [];
      const bySection = new Map<string, PickType[]>();
      for (const t of types) {
        if (sideOf(t) !== side || (q && !matches(t))) continue;
        if (!bySection.has(t.section)) { bySection.set(t.section, []); order.push(t.section); }
        bySection.get(t.section)!.push(t);
      }
      order.forEach((s, i) => groups.push({
        key: `${side ? 'aside' : 'main'}:${s}`, label: s, items: bySection.get(s)!.slice().sort(byName),
        heading: side && i === 0 ? aside?.label : undefined,
      }));
    }
    return groups;
  }, [types, q, asideShown, aside]); // eslint-disable-line react-hooks/exhaustive-deps

  const flat = useMemo(() => sections.flatMap(g => g.items), [sections]);

  // Строка «нет значения» показывается только без запроса (поиск подразумевает выбор реального типа).
  // Занимает индекс 0; типы сдвигаются на 1 (base).
  const noneVisible = !!noneOption && !q;
  const base = noneVisible ? 1 : 0;
  const total = base + flat.length;



  // «Назад» на телефоне закрывает выбор, а не страницу под ним (issue #1418).
  useHistoryLayer(true, () => { onOpenChange(false); return true; });
  function choose(t: PickType) { pushRecent(t.id); onSelect(t.id); onOpenChange(false); }
  function chooseNone() { onSelectNone?.(); onOpenChange(false); }
  /** Выход: кнопка исчезает, и фокус обязан вернуться в поиск — иначе стрелки и Enter молчат. */
  function reveal() { setRevealed(true); input.current?.focus(); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, total - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') {
      e.preventDefault();
      if (noneVisible && active === 0) { chooseNone(); return; }
      const t = flat[active - base];
      if (t) choose(t);
      // Список пуст, а выход есть — Enter раскрывает: с клавиатуры иначе пришлось бы искать кнопку.
      else if (asideHidden) reveal();
    }
  }

  let idx = base - 1; // сквозной индекс строки типа (после none-строки, если она есть); ++idx даёт base
  return (
    <Dialog.Root open onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-scrim/32" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          data-modal-state="free"
          className="fixed left-1/2 top-16 z-50 w-[calc(100vw-2rem)] max-w-lg -translate-x-1/2 flex flex-col max-h-[calc(100vh-8rem)] overflow-hidden rounded-lg border border-outline-variant bg-surface focus:outline-none shadow-elevation-2"
        >
          <Dialog.Title className="sr-only">{title}</Dialog.Title>
          <div className="flex items-center gap-2 border-b border-outline-variant px-4 shrink-0">
            <Icon icon={Search} size={18} className="shrink-0 text-on-surface-variant" />
            {/* Выделение возвращает на первую строку сам ввод, а не эффект (issue #858). */}
            <input ref={input} autoFocus value={query} onChange={e => { setQuery(e.target.value); setActive(0); }} onKeyDown={onKey}
              placeholder="Поиск типа по имени или коду…" aria-label="Поиск типа"
              className="h-12 flex-1 bg-transparent text-body-medium text-on-surface outline-none placeholder:text-on-surface-variant" />
            <kbd className="rounded-xs border border-outline-variant px-1 text-label-small text-on-surface-variant">Esc</kbd>
          </div>
          <ul className="overflow-y-auto p-2" role="listbox" aria-label={title}>
            {noneVisible && (
              <li role="option" aria-selected={active === 0}>
                <button type="button" onMouseEnter={() => setActive(0)} onClick={chooseNone}
                  className={`flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-body-medium ${
                    active === 0 ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'}`}>
                  <Icon icon={Block} size={18} className={active === 0 ? 'text-on-secondary-container' : 'text-on-surface-variant'} />
                  <span className="flex-1 truncate" title={noneOption!.label}>{noneOption!.label}</span>
                </button>
              </li>
            )}
            {/* Пусто, но выход есть — «Ничего не найдено» было бы неправдой: найдено, но в стороне. */}
            {flat.length === 0 ? (
              !noneVisible && !asideHidden && <li className="px-3 py-6 text-center text-body-medium text-on-surface-variant">Ничего не найдено</li>
            ) : sections.map(g => (
              <li key={g.key}>
                {g.heading && (
                  <div className="mt-2 border-t border-outline-variant px-3 pt-3 pb-1 text-body-small font-medium text-on-surface-variant">{g.heading}</div>
                )}
                <div className="px-3 pt-2 pb-1 text-title-small text-on-surface-variant">{g.label}</div>
                <ul>
                  {g.items.map(t => {
                    const i = ++idx;
                    const icon = typeIcon(t);
                    const code = t.code.trim();
                    const showCode = !!code && code.toLowerCase() !== t.name.trim().toLowerCase();
                    return (
                      <li key={`${g.key}:${t.id}`} role="option" aria-selected={i === active}>
                        <button type="button" onMouseEnter={() => setActive(i)} onClick={() => choose(t)}
                          className={`flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-body-medium ${
                            i === active ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'}`}>
                          <Icon icon={icon} size={18} className={i === active ? 'text-on-secondary-container' : 'text-on-surface-variant'} />
                          <Tooltip content={showCode ? `${t.name} (${code})` : t.name}><span className="flex-1 truncate">{t.name}</span></Tooltip>
                          {showCode && <span className={`text-label-small font-mono shrink-0 ${i === active ? 'text-on-secondary-container/80' : 'text-on-surface-variant'}`}>{code}</span>}
                        </button>
                      </li>
                    );
                  })}
                </ul>
              </li>
            ))}
          </ul>
          {asideHidden && aside && (
            <div className="shrink-0 border-t border-outline-variant p-2">
              <button type="button" onClick={reveal}
                className="w-full rounded-md px-3 py-2 text-left text-body-medium text-primary state-layer focus-ring">
                {aside.reveal}
              </button>
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
