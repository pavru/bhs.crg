import * as Dialog from '@radix-ui/react-dialog';
import { Search, Sun, Moon, Monitor, LogOut, CornerDownLeft, type LucideIcon } from 'lucide-react';
import { Fragment, useMemo, useState } from 'react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useTheme } from '@/shared/ui/themeContext';
import { workNav, settingsNav } from './navConfig';
import { railDef } from './railSections';
import { visibleNav } from './navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useGuardedLeave, useGuardedNavigate } from './NavigationGuard';
import { useWorkspace } from './workspaceContext';
import { Chip } from './Chip';
import { availableActions, intentHref } from './paletteActions';
import { highlight, matches, queryWords, ranked } from './paletteSearch';

type Kind = 'action' | 'section';

interface Cmd {
  id: string;
  label: string;
  /** Слова, по которым пункт ещё ищется, кроме подписи. */
  words: string;
  /** Группа в списке: «Действия» или раздел рейла. */
  group: string;
  kind: Kind;
  icon: LucideIcon;
  run: () => void;
}

/** Где искать. Чипы данных («Счета», «Стройки»…) придут с поиском по данным — второй PR #1433. */
const SCOPES: { id: 'all' | Kind; label: string }[] = [
  { id: 'all', label: 'Везде' },
  { id: 'action', label: 'Действия' },
  { id: 'section', label: 'Разделы' },
];

const ACTIONS = 'Действия';

/**
 * Командная палитра (issue #107, Ctrl+K) — один вход в «сделать» и «перейти» (issue #1433).
 * Стрелки ↑↓ двигают выделение, Enter исполняет, Esc закрывает, ввод ищет по словам.
 * Открывается глобальным Ctrl/⌘+K (слушатель в AppShell) и кнопкой поиска в шапке.
 *
 * <p>Действия — «Новый счёт», «Счёт из файла», «Новая накладная», «Новая стройка» — исполняет экран
 * своей кнопкой (`paletteActions.ts`); здесь только переход к нему. Плюс тема и выход.</p>
 *
 * <p>Тело монтируется по открытию (issue #858): строка поиска и выделение заводятся заново, и
 * эффект «закрылось — очисти запрос» не нужен.</p>
 */
export function CommandPalette(props: { open: boolean; onOpenChange: (o: boolean) => void }) {
  return props.open ? <CommandPaletteBody {...props} /> : null;
}

function CommandPaletteBody({ onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  // Палитра — второй вход в те же разделы, что и рейл, и уходить обязана тем же путём — через
  // охрану несохранённых правок (issue #1238). С прямым `navigate` счёт с правками терялся за два
  // нажатия, без вопроса; то же — команда выхода.
  const navigate = useGuardedNavigate();
  const leave = useGuardedLeave();
  const { logout } = useAuth();
  const { setTheme } = useTheme();
  // Те же права, что и у сайдбара, и из того же источника (issue #952). Палитра — второй вход в
  // те же разделы: отфильтруй сайдбар и забудь палитру — и скрытый раздел останется доступен по
  // Ctrl+K, то есть «скрыт» он будет только на вид. То же с действиями: каждое — по праву своей кнопки.
  const { data: access } = useAccess();
  const work = visibleNav(workNav, access ?? NO_ACCESS);
  const settings = visibleNav(settingsNav, access ?? NO_ACCESS);
  const actions = availableActions(access ?? NO_ACCESS);
  // Палитра ищет разделы ВСЕХ доступных модулей, а не только пространства (ТЗ AUTH-16.1, issue
  // #1001): иначе скрытый раздел недостижим ни меню, ни палитрой. Найденное вне пространства помечено.
  const { inside } = useWorkspace();

  const items = useMemo<Cmd[]>(() => {
    const act = (id: string, label: string, words: string, icon: LucideIcon, run: () => void): Cmd =>
      ({ id, label, words, group: ACTIONS, kind: 'action', icon, run });
    const nav = [
      // Группы палитры — разделы рейла (issue #1429): найденное подписано тем же именем, под которым
      // его искать в меню.
      ...work.map(n => ({ ...n, group: inside(n) ? railDef(n.rail).title : 'Вне пространства' })),
      ...settings.map(n => ({ ...n, group: railDef(n.rail).title })),
    ].map<Cmd>(n => ({
      id: n.to, label: n.label, words: n.group, group: n.group, kind: 'section', icon: n.icon, run: () => navigate(n.to),
    }));
    return [
      ...actions.map(a => act(a.id, a.label, a.words, a.icon, () => navigate(intentHref(a)))),
      act('theme-light', 'Тема: светлая', 'оформление день', Sun, () => setTheme('light')),
      act('theme-dark', 'Тема: тёмная', 'оформление ночь', Moon, () => setTheme('dark')),
      act('theme-system', 'Тема: авто (как в системе)', 'оформление', Monitor, () => setTheme('system')),
      act('logout', 'Выйти', 'выход из системы', LogOut, () => leave(logout)),
      ...nav,
    ];
  }, [work, settings, actions, inside, navigate, leave, setTheme, logout]);

  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<'all' | Kind>('all');
  const [active, setActive] = useState(0);
  const words = queryWords(query);
  const filtered = ranked(items.filter(i => (scope === 'all' || i.kind === scope) && matches(i.label, i.words, words)), words);

  // Сначала закрыться, потом исполнить: вопрос об уходе задаёт страница, своим окном, — и оно не
  // должно открываться под палитрой.
  function exec(it: Cmd) { onOpenChange(false); it.run(); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, filtered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const it = filtered[active]; if (it) exec(it); }
  }

  return (
    <Dialog.Root open onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          data-modal-state="transient"
          aria-describedby={undefined}
          className="fixed left-1/2 top-16 z-50 w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 overflow-hidden rounded-[28px] border border-stroke bg-surface focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}
        >
          <Dialog.Title className="sr-only">Поиск и действия</Dialog.Title>
          <div className="flex items-center gap-3 border-b border-stroke pl-5 pr-4">
            <Search size={18} aria-hidden className="shrink-0 text-brand" />
            <input
              autoFocus value={query} onKeyDown={onKey} aria-label="Поиск и действия"
              onChange={e => { setQuery(e.target.value); setActive(0); }}
              placeholder="Найти раздел или действие…"
              className="h-14 flex-1 bg-transparent text-base text-fg1 outline-none placeholder:text-fg4"
            />
            <kbd className="rounded-md bg-muted px-2 py-0.5 font-mono text-caption text-fg3">Esc</kbd>
          </div>

          <div role="group" aria-label="Где искать" className="flex flex-wrap gap-2 px-5 pt-3 pb-1">
            {SCOPES.map(s => (
              <Chip key={s.id} selected={scope === s.id} onClick={() => { setScope(s.id); setActive(0); }}>
                {s.label}
              </Chip>
            ))}
          </div>

          <ul className="max-h-[55vh] overflow-y-auto px-2 pb-3 pt-1">
            {filtered.length === 0 ? (
              <li className="px-3 py-6 text-center text-sm text-fg4">Ничего не найдено</li>
            ) : filtered.map((it, i) => (
              <Fragment key={it.id}>
                {it.group !== filtered[i - 1]?.group && (
                  <li aria-hidden className="px-3 pb-1 pt-3 text-xs font-medium text-fg3">{it.group}</li>
                )}
                <li>
                  <button
                    type="button" onMouseEnter={() => setActive(i)} onClick={() => exec(it)}
                    className={`flex w-full items-center gap-3 rounded-xl px-3 py-2 text-left text-sm transition-colors ${
                      i === active ? 'bg-tonal text-on-tonal' : 'text-fg1'}`}
                  >
                    <span className={`flex size-8 shrink-0 items-center justify-center rounded-lg ${
                      it.kind === 'action' ? 'bg-brand-subtle text-on-brand-subtle' : 'bg-muted text-fg3'}`}>
                      <it.icon size={16} aria-hidden />
                    </span>
                    <span className="flex-1">
                      {highlight(it.label, words).map((p, k) => p.hit
                        ? <mark key={k} className="rounded-sm bg-warning-subtle text-inherit">{p.text}</mark>
                        : <Fragment key={k}>{p.text}</Fragment>)}
                    </span>
                    {i === active && (
                      <span className="flex items-center gap-1 text-caption">
                        Enter <CornerDownLeft size={14} aria-hidden />
                      </span>
                    )}
                  </button>
                </li>
              </Fragment>
            ))}
          </ul>

          <div className="flex flex-wrap gap-x-5 gap-y-1.5 border-t border-stroke bg-base px-5 py-3 text-caption text-fg3">
            <span><b className="font-medium">↑ ↓</b> выбрать</span>
            <span><b className="font-medium">Enter</b> открыть</span>
            <span className="flex-1" />
            <span>Подсказка: напишите «тёмная тема» или «новый счёт»</span>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
