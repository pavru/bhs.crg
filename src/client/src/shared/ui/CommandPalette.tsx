import * as Dialog from '@radix-ui/react-dialog';
import { Icon } from './Icon';
import { DarkMode, KeyboardReturn, LightMode, Logout, Monitor, Search, type IconDef } from './icons';
import { Fragment, useEffect, useRef, useState } from 'react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useTheme } from '@/shared/ui/themeContext';
import { workNav, settingsNav } from './navConfig';
import { railDef } from './railSections';
import { visibleNav } from './navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useGuardedIntent, useGuardedLeave, useGuardedNavigate } from './NavigationGuard';
import { useHistoryLayer } from './historyLayers';
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
  icon: IconDef;
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
  // «Назад» на телефоне закрывает палитру, а не страницу под ней (issue #1418).
  useHistoryLayer(true, () => { onOpenChange(false); return true; });
  // Палитра — второй вход в те же разделы, что и рейл, и уходить обязана тем же путём — через
  // охрану несохранённых правок (issue #1238). С прямым `navigate` счёт с правками терялся за два
  // нажатия, без вопроса; то же — команда выхода.
  const navigate = useGuardedNavigate();
  // Действие ведёт на свой экран: на другой — через охрану, на тот же — без неё, и о правках спросит
  // кнопка экрана, а не охрана перед ней (ревью PR #1478: вопрос задавался дважды).
  const toScreen = useGuardedIntent();
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

  // Без useMemo: источники — новые массивы на каждую отрисовку, и запоминание не срабатывало бы
  // никогда (ревью PR #1478); список из трёх десятков пунктов дешевле, чем видимость оптимизации.
  const act = (id: string, label: string, words: string, icon: IconDef, run: () => void): Cmd =>
    ({ id, label, words, group: ACTIONS, kind: 'action', icon, run });
  const nav = [
      // Группы палитры — разделы рейла (issue #1429): найденное подписано тем же именем, под которым
      // его искать в меню.
    ...work.map(n => ({ ...n, group: inside(n) ? railDef(n.rail).title : 'Вне пространства' })),
    ...settings.map(n => ({ ...n, group: railDef(n.rail).title })),
  ].map<Cmd>(n => ({
    id: n.to, label: n.label, words: n.group, group: n.group, kind: 'section', icon: n.icon, run: () => navigate(n.to),
  }));
  const items: Cmd[] = [
    ...actions.map(a => act(a.id, a.label, a.words, a.icon, () => toScreen(intentHref(a)))),
    act('theme-light', 'Тема: светлая', 'оформление день', LightMode, () => setTheme('light')),
    act('theme-dark', 'Тема: тёмная', 'оформление ночь', DarkMode, () => setTheme('dark')),
    act('theme-system', 'Тема: авто (как в системе)', 'оформление', Monitor, () => setTheme('system')),
    act('logout', 'Выйти', 'выход из системы', Logout, () => leave(logout)),
    ...nav,
  ];

  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<'all' | Kind>('all');
  const [active, setActive] = useState(0);
  const words = queryWords(query);
  const filtered = ranked(items.filter(i => (scope === 'all' || i.kind === scope) && matches(i.label, i.words, words)), words);
  // Выдача может сократиться сама — пришли права, сменилось пространство, — и выделение за её концом
  // не показывалось бы, а Enter молчал (ревью PR #1478).
  const current = Math.min(active, filtered.length - 1);
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLUListElement>(null);
  // Выделение стрелками не уходит за край списка: список длиннее окна.
  useEffect(() => {
    list.current?.querySelector('[data-active="true"]')?.scrollIntoView?.({ block: 'nearest' });
  }, [current, query, scope]);
  // Пример запроса — из того, что человеку доступно: «новый счёт» тому, у кого нет такой кнопки, был бы
  // дверью в «Ничего не найдено».
  const example = actions[0]?.label.toLowerCase() ?? 'выйти';

  // Сначала закрыться, потом исполнить: вопрос об уходе задаёт страница, своим окном, — и оно не
  // должно открываться под палитрой.
  function exec(it: Cmd) { onOpenChange(false); it.run(); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, filtered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const it = filtered[current]; if (it) exec(it); }
  }

  return (
    <Dialog.Root open onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          data-modal-state="transient"
          aria-describedby={undefined}
          className="fixed left-1/2 top-16 z-50 w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2 overflow-hidden rounded-[28px] border border-outline-variant bg-surface focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}
        >
          <Dialog.Title className="sr-only">Поиск и действия</Dialog.Title>
          <div className="flex items-center gap-3 border-b border-outline-variant pl-5 pr-4">
            <Icon icon={Search} size={18} aria-hidden className="shrink-0 text-primary" />
            <input
              ref={input} autoFocus value={query} onKeyDown={onKey} aria-label="Поиск и действия"
              onChange={e => { setQuery(e.target.value); setActive(0); }}
              placeholder="Найти раздел или действие…"
              className="h-14 flex-1 bg-transparent text-base text-on-surface outline-none placeholder:text-on-surface-variant"
            />
            <kbd className="rounded-md bg-surface-container px-2 py-0.5 font-mono text-caption text-on-surface-variant">Esc</kbd>
          </div>

          <div role="group" aria-label="Где искать" className="flex flex-wrap gap-2 px-5 pt-3 pb-1">
            {SCOPES.map(s => (
              // Фокус возвращается в поле: стрелки и Enter слушает оно, и после щелчка по чипу клавиатура
              // палитру иначе больше не вела бы (ревью PR #1478).
              <Chip key={s.id} selected={scope === s.id} onClick={() => { setScope(s.id); setActive(0); input.current?.focus(); }}>
                {s.label}
              </Chip>
            ))}
          </div>

          <ul ref={list} className="max-h-[55vh] overflow-y-auto px-2 pb-3 pt-1">
            {filtered.length === 0 ? (
              <li className="px-3 py-6 text-center text-sm text-on-surface-variant">Ничего не найдено</li>
            ) : filtered.map((it, i) => (
              <Fragment key={it.id}>
                {it.group !== filtered[i - 1]?.group && (
                  <li aria-hidden className="px-3 pb-1 pt-3 text-xs font-medium text-on-surface-variant">{it.group}</li>
                )}
                <li>
                  <button
                    type="button" onMouseEnter={() => setActive(i)} onClick={() => exec(it)} data-active={i === current}
                    className={`flex w-full items-center gap-3 rounded-xl px-3 py-2 text-left text-sm transition-colors ${
                      i === current ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface'}`}
                  >
                    <span className={`flex size-8 shrink-0 items-center justify-center rounded-lg ${
                      it.kind === 'action' ? 'bg-primary-container text-on-primary-container' : 'bg-surface-container text-on-surface-variant'}`}>
                      <Icon icon={it.icon} size={18} aria-hidden />
                    </span>
                    <span className="flex-1">
                      {highlight(it.label, words).map((p, k) => p.hit
                        ? <mark key={k} className="rounded-sm bg-warning-container text-inherit">{p.text}</mark>
                        : <Fragment key={k}>{p.text}</Fragment>)}
                      {/* Заголовок группы диктору не читается — группа звучит у самого пункта: «Вне
                          пространства» иначе слышно не было бы (ревью PR #1478). */}
                      <span className="sr-only">, {it.group}</span>
                    </span>
                    {i === current && (
                      <span className="flex items-center gap-1 text-caption">
                        Enter <Icon icon={KeyboardReturn} size={18} aria-hidden />
                      </span>
                    )}
                  </button>
                </li>
              </Fragment>
            ))}
          </ul>

          <div className="flex flex-wrap gap-x-5 gap-y-1.5 border-t border-outline-variant bg-surface-container-low px-5 py-3 text-caption text-on-surface-variant">
            <span><b className="font-medium">↑ ↓</b> выбрать</span>
            <span><b className="font-medium">Enter</b> открыть</span>
            <span className="flex-1" />
            <span>Подсказка: напишите «тёмная тема» или «{example}»</span>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
