import * as Dialog from '@radix-ui/react-dialog';
import { Icon } from './Icon';
import { ArrowBack, ArrowForward, DarkMode, KeyboardReturn, LightMode, Logout, Monitor, Search,
  type IconDef } from './icons';
import { Fragment, useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type RefObject } from 'react';
import { useLocation } from 'react-router';
import { dockedPlace, fullPlace, PALETTE_SURFACE } from './paletteDock';
import { IconButton } from './Button';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
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
import { ListOption } from './ListOption';
import { availableActions, intentHref, recordActions } from './paletteActions';
import { highlight, matches, queryWords, ranked } from './paletteSearch';
import { foundGroups, hitText, openGroups, tabGroup, type FoundGroup } from './paletteRecords';
import { hitRoute, recordHref } from './recordRoutes';
import { SEARCH_MAX_LENGTH, useRecordSearch } from '@/shared/api/recordSearch';
import type { AccessInfo } from '@/shared/api/access';

type Kind = 'action' | 'section' | 'record';

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
  /** Найдено сервером (запись и действие с ней): клиентом не отбирается, а подписью раздела уступает. */
  found?: true;
  /** Действие с найденной записью: уступает самой записи — Enter открывает запись ([UI-30]), а не заводит
   *  черновик (ревью #1915). */
  trails?: true;
  /** Группа данных записи — для чипа её области. */
  area?: string;
  supporting?: string;
  trailing?: string;
}

/** Где искать: «Везде», группы данных — те, что открыты человеку ([UI-31]), — «Действия», «Разделы». */
type Scope = 'all' | 'action' | 'section' | (string & {});

const ACTIONS = 'Действия';

/**
 * Командная палитра (issue #107, Ctrl+K) — один вход в «сделать» и «перейти» (issue #1433).
 * Стрелки ↑↓ двигают выделение, Enter исполняет, Esc закрывает, ввод ищет по словам.
 * Открывается глобальным Ctrl/⌘+K (слушатель в AppShell) и кнопкой поиска в шапке.
 *
 * <p>Действия — «Новый счёт», «Счёт из файла», «Новая накладная», «Новая стройка» — исполняет экран
 * своей кнопкой (`paletteActions.ts`); здесь только переход к нему. Плюс тема и выход.</p>
 *
 * <p>Записи — счета, стройки, наборы данных — ищет сервер в правах спрашивающего (shell-05, [UI-30]); у
 * найденной записи — действие её экрана с ней ([UI-32], «Новая накладная на стройку „…“»). Отказ поиска
 * сказан словами, а не «Ничего не найдено». Все строки — `ListOption` (М-2): фокус живёт в поле.</p>
 *
 * <p>Тело монтируется по открытию (issue #858): строка поиска и выделение заводятся заново, и
 * эффект «закрылось — очисти запрос» не нужен.</p>
 */
type PaletteProps = {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  /** Строка поиска в шапке: палитра встаёт на её место. Без неё — по центру под полосой. */
  anchorRef?: RefObject<HTMLElement | null>;
};

export function CommandPalette(props: PaletteProps) {
  return props.open ? <CommandPaletteBody {...props} /> : null;
}

function usePalettePlace(anchorRef: PaletteProps['anchorRef'], full: boolean) {
  const [place, setPlace] = useState<CSSProperties | null>(null);
  // Место меряется до первого кадра (layout-эффект), и заново — когда окно сменило размер: строка
  // поиска переехала или ужалась, палитра идёт за ней. На телефоне — когда видимую область сжала или
  // сдвинула экранная клавиатура: о ней знает только `visualViewport`.
  useLayoutEffect(() => {
    const view = window.visualViewport;
    const measure = () => setPlace(full ? fullPlace(view) : dockedPlace(anchorRef?.current, window.innerWidth, true));
    measure();
    window.addEventListener('resize', measure);
    view?.addEventListener('resize', measure);
    view?.addEventListener('scroll', measure);
    return () => {
      window.removeEventListener('resize', measure);
      view?.removeEventListener('resize', measure);
      view?.removeEventListener('scroll', measure);
    };
  }, [anchorRef, full]);
  return place;
}

function CommandPaletteBody({ onOpenChange, anchorRef }: PaletteProps) {
  // На телефоне — full-screen search view MD3: поле во всю ширину, «Назад» вместо знака поиска.
  const full = !useMedia(WIDE);
  const place = usePalettePlace(anchorRef, full);
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
  const { pathname } = useLocation();
  const groups = openGroups(access ?? NO_ACCESS);
  // Tab сужает поиск до группы раздела, где человек ([UI-31]); на экране без своей группы — ничего.
  const tab = tabGroup(pathname, groups);

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
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<Scope>('all');
  // Выделение — пунктом, а не местом: пришёл ответ поиска, записи встали между действиями и разделами, —
  // и Enter обязан исполнить выделенное стрелкой, а не то, что встало на его место (ревью #1915). `null` —
  // стрелкой не выбирали: выделен первый.
  const [activeId, setActiveId] = useState<string | null>(null);
  const words = queryWords(query);
  const area = groups.some(g => g.def.code === scope) ? scope : null;
  // Без групп данных — ни своих, ни ещё не пришедших прав — по данным не искать: запрос впустую, а «Ищу
  // записи…» и отказ поиска сказаны бы о том, чего человек не ищет (ревью #1915).
  const search = useRecordSearch(query, groups.length > 0 && (scope === 'all' || area !== null));
  const found = foundGroups(search.groups, groups);
  const { records, withRecord } = recordCmds(found, inside, access ?? NO_ACCESS, navigate, toScreen);
  const items: Cmd[] = [
    // Действия с найденной записью — в начале «Действий» (М-3).
    ...withRecord,
    ...actions.map(a => act(a.id, a.label, a.words, a.icon, () => toScreen(intentHref(a)))),
    act('theme-light', 'Тема: светлая', 'оформление день', LightMode, () => setTheme('light')),
    act('theme-dark', 'Тема: тёмная', 'оформление ночь', DarkMode, () => setTheme('dark')),
    act('theme-system', 'Тема: авто (как в системе)', 'оформление', Monitor, () => setTheme('system')),
    act('logout', 'Выйти', 'выход из системы', Logout, () => leave(logout)),
    ...records,
    ...nav,
  ];

  const filtered = ranked(items.filter(i =>
    (scope === 'all' || i.kind === scope || i.area === scope) && (i.found || matches(i.label, i.words, words))), words);
  const refusals = [
    ...(search.failed ? ['Записи не найдены: поиск не ответил. Действия и разделы ищутся как прежде.'] : []),
    ...(search.tooLong ? [`Записи не ищутся: запрос длиннее ${SEARCH_MAX_LENGTH} знаков.`] : []),
    ...found.filter(f => f.answer.refused).map(f => `${f.def.label} не найдены: ${f.answer.refused}`),
  ];
  const listId = useId();
  const optionId = (i: number) => `${listId}-${i}`;
  // Выдача может сократиться сама — пришли права, сменилось пространство, — и выделение за её концом
  // не показывалось бы, а Enter молчал (ревью PR #1478).
  const current = Math.max(0, filtered.findIndex(it => it.id === activeId));
  const input = useRef<HTMLInputElement>(null);
  // Выделение стрелками не уходит за край списка: список длиннее окна.
  useEffect(() => {
    document.getElementById(`${listId}-${current}`)?.scrollIntoView?.({ block: 'nearest' });
  }, [current, query, scope, listId]);
  const choose = (s: Scope) => { setScope(s); setActiveId(null); };
  const step = (by: number) => setActiveId(filtered[Math.min(Math.max(current + by, 0), filtered.length - 1)]?.id ?? null);
  // Пример запроса — из того, что человеку доступно: «новый счёт» тому, у кого нет такой кнопки, был бы
  // дверью в «Ничего не найдено».
  const example = actions[0]?.label.toLowerCase() ?? 'выйти';

  // Сначала закрыться, потом исполнить: вопрос об уходе задаёт страница, своим окном, — и оно не
  // должно открываться под палитрой.
  function exec(it: Cmd) { onOpenChange(false); it.run(); }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); step(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); step(-1); }
    else if (e.key === 'Enter') { e.preventDefault(); const it = filtered[current]; if (it) exec(it); }
    // Tab остаётся в поле (палитра — модальное окно, Esc её закрывает): сужает до группы раздела, повторный
    // — «Везде». На экране без своей группы область не меняется. Shift+Tab не перехватывается.
    else if (e.key === 'Tab' && !e.shiftKey) {
      e.preventDefault();
      if (tab) choose(scope === tab.def.code ? 'all' : tab.def.code);
    }
  }
  const scopes: { id: Scope; label: string }[] = [
    { id: 'all', label: 'Везде' },
    ...groups.map(g => ({ id: g.def.code, label: g.def.label })),
    { id: 'action', label: 'Действия' },
    { id: 'section', label: 'Разделы' },
  ];

  return (
    <Dialog.Root open onOpenChange={onOpenChange}>
      <Dialog.Portal>
        {/* Scrim без размытия — по MD3: размытый фон читался как «страница сломалась». */}
        <Dialog.Overlay className="fixed inset-0 z-40 bg-scrim/32" />
        <Dialog.Content
          data-modal-state="transient" aria-describedby={undefined} style={place ?? undefined}
          className={`fixed z-50 overflow-hidden text-on-surface focus:outline-none ${
            full ? 'inset-0 flex flex-col bg-surface-container-high'
              : `rounded-xl ${PALETTE_SURFACE} ${place ? '' : 'left-1/2 top-16 w-[calc(100%-2rem)] max-w-2xl -translate-x-1/2'}`}`}
        >
          <Dialog.Title className="sr-only">Поиск и действия</Dialog.Title>
          <div className={`flex items-center border-b border-outline-variant ${full ? 'shrink-0 h-18 gap-1 pl-1 pr-4' : 'gap-4 px-4'}`}>
            {full
              ? <IconButton label="Назад" icon={ArrowBack} onClick={() => onOpenChange(false)} />
              : <Icon icon={Search} size={24} aria-hidden className="shrink-0 text-on-surface" />}
            <input
              ref={input} autoFocus value={query} onKeyDown={onKey} aria-label="Поиск и действия"
              aria-controls={listId} aria-activedescendant={filtered[current] ? optionId(current) : undefined}
              onChange={e => { setQuery(e.target.value); setActiveId(null); }}
              placeholder={groups.length ? 'Найти запись, раздел или действие…' : 'Найти раздел или действие…'}
              className="h-14 flex-1 bg-transparent text-body-large text-on-surface outline-none placeholder:text-on-surface-variant"
            />
            {!full && <kbd className="rounded-sm bg-surface-container-high px-2 py-0.5 font-mono text-label-small text-on-surface-variant">Esc</kbd>}
          </div>

          <div role="group" aria-label="Где искать" className="flex flex-wrap gap-2 px-5 pt-3 pb-1">
            {scopes.map(s => (
              // Фокус возвращается в поле: стрелки и Enter слушает оно, и после щелчка по чипу клавиатура
              // палитру иначе больше не вела бы (ревью PR #1478).
              <Chip key={s.id} selected={scope === s.id} onClick={() => { choose(s.id); input.current?.focus(); }}>
                {s.label}
              </Chip>
            ))}
          </div>

          {/* Отказ ≠ пусто: сказан словами и диктору, а действия и разделы ищутся как прежде. Живая область
              стоит всегда — читалки объявляют изменение в подключённой области, а не вставку её с текстом. */}
          <div role="status" aria-label="Отказ поиска"
            className={refusals.length > 0 ? 'px-5 pt-2 text-body-small text-error' : undefined}>
            {refusals.map(r => <p key={r}>{r}</p>)}
          </div>

          <div id={listId} role="listbox" aria-label="Найденное"
            className={`${full ? 'flex-1 min-h-0' : 'max-h-[55vh]'} overflow-y-auto pb-3 pt-1`}>
            {filtered.length === 0 ? (
              refusals.length > 0 ? null : (
                <p role="presentation" className="px-3 py-6 text-center text-body-medium text-on-surface-variant">
                  {search.pending ? 'Ищу записи…' : 'Ничего не найдено'}
                </p>
              )
            ) : filtered.map((it, i) => (
              <Fragment key={it.id}>
                {it.group !== filtered[i - 1]?.group && (
                  <div role="presentation" aria-hidden className="px-4 pb-1 pt-3 text-body-small font-medium text-on-surface-variant">{it.group}</div>
                )}
                <ListOption
                  id={optionId(i)} active={i === current} icon={it.icon} onChoose={() => exec(it)} onHover={() => setActiveId(it.id)}
                  supporting={it.supporting}
                  trailing={(it.trailing || (i === current && !full)) && <>
                    {it.trailing}
                    {i === current && !full && (
                      <span className="flex items-center gap-1 text-label-small">
                        Enter <Icon icon={KeyboardReturn} size={18} aria-hidden />
                      </span>
                    )}
                  </>}
                  headline={<span data-palette-label>
                    {highlight(it.label, words).map((p, k) => p.hit
                      ? <mark key={k} className="rounded-xs bg-warning-container text-inherit">{p.text}</mark>
                      : <Fragment key={k}>{p.text}</Fragment>)}
                    {/* Заголовок группы диктору не читается — группа звучит у самого пункта: «Вне
                        пространства» иначе слышно не было бы (ревью PR #1478). */}
                    <span className="sr-only">, {it.group}</span>
                  </span>}
                />
              </Fragment>
            ))}
          </div>

          <div className="flex flex-wrap gap-x-5 gap-y-1.5 border-t border-outline-variant px-5 py-3 text-label-small text-on-surface-variant">
            {/* Клавиши — рабочему столу: на телефоне клавиатуры нет, подсказка запроса остаётся. */}
            {!full && <>
              <span><b className="font-medium">↑ ↓</b> выбрать</span>
              <span><b className="font-medium">Enter</b> открыть</span>
              {/* Строка о Tab — только там, где у раздела своя группа данных ([UI-31]). */}
              {tab && <span><b className="font-medium">Tab</b> {scope === tab.def.code ? 'искать везде' : `искать только в ${tab.def.within}`}</span>}
              <span className="flex-1" />
            </>}
            <span>Подсказка: напишите «тёмная тема» или «{example}»</span>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/**
 * Строки найденных записей и действия с ними ([UI-30], [UI-32]). Группа записи вне пространства помечена
 * в своём заголовке, и пометку слышит диктор у каждой строки (`AUTH-16.1`: пространство не граница).
 */
function recordCmds(found: FoundGroup[], inside: (item: { module?: string; section?: string }) => boolean,
  access: AccessInfo, navigate: (to: string) => void, toScreen: (to: string) => void) {
  const records: Cmd[] = [];
  const withRecord: Cmd[] = [];
  for (const { def, route, answer } of found) {
    const screen = [...workNav, ...settingsNav].find(n => n.to === route.screen);
    const group = screen && !inside(screen) ? `${def.label} — вне пространства` : def.label;
    const actions = recordActions(access, def.recordType);
    for (const hit of answer.items) {
      // Экран записи — по её уровню (набор данных): закрытый или неизвестный — строки нет, а не отказ за ней.
      const at = hitRoute(route, hit.level, access);
      if (!at) continue;
      records.push({
        id: `${def.code}:${hit.id}`, label: hit.title, words: '', group, kind: 'record', area: def.code, found: true,
        icon: def.icon, run: () => navigate(recordHref(at, hit.id)), ...hitText(hit),
      });
      for (const a of actions) withRecord.push({
        id: `${a.id}:${hit.id}`, label: a.withRecord!.label(hit.title), words: '', group: ACTIONS, kind: 'action',
        found: true, trails: true, icon: a.icon, run: () => toScreen(intentHref(a, hit.id)),
      });
    }
    // «Ещё в разделе»: экраны запроса из адреса пока не принимают — строка ведёт в раздел.
    if (answer.more) records.push({
      id: `${def.code}:more`, label: `Ещё в разделе «${def.label}»`, words: '', group, kind: 'record',
      area: def.code, found: true, icon: ArrowForward, run: () => navigate(route.screen),
    });
  }
  return { records, withRecord };
}
