import { useState, useEffect, useRef, type CSSProperties, type Ref } from 'react';
import { Link, Outlet, useLocation } from 'react-router';
import { useAccount, useResendConfirmation } from '@/shared/api/account';
import { useAccess } from '@/shared/api/access';
import { ActivityCenter } from '@/features/activity/ActivityCenter';
import { CommandPalette } from '@/shared/ui/CommandPalette';
import { AccountMenu } from '@/shared/ui/AccountMenu';
import { ShortcutsHelp } from '@/shared/ui/ShortcutsHelp';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { paletteShown, refusedByDialog } from '@/shared/ui/modalDismissal';
import { WorkspaceProvider } from '@/shared/ui/WorkspaceProvider';
import { WorkspaceSwitcher } from '@/shared/ui/WorkspaceSwitcher';
import { OutsideBanner } from '@/shared/ui/WorkspaceNav';
import { SiteFilterProvider } from '@/shared/siteFilter/SiteFilterProvider';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
import { BottomNav } from '@/shared/ui/BottomNav';
import { SectionMenuContext } from '@/shared/ui/sectionMenuContext';
import { phoneChrome } from '@/shared/ui/phoneChrome';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { PrimaryFab } from '@/shared/ui/PrimaryFab';
import { PrimaryFabContext, usePrimaryFabState } from '@/shared/ui/primaryFabContext';
import { FAB_CLEARANCE, snackbarLift } from '@/shared/ui/phoneLayout';
import { useSnackbarPlace } from '@/shared/ui/snackbarPlace';
import { primaryActionAt } from '@/shared/ui/paletteActions';
import { SectionPanel, SectionPanelDrawer, SectionRail, ShowPanelButton } from '@/shared/ui/SectionRail';
import { SuppressSectionPanelContext, useSectionPanel } from '@/shared/ui/sectionPanelSuppress';
import { useFavoriteSitesGroup, withFavoriteSites } from '@/shared/siteFilter/favoriteSitesGroup';
import { useNavSections } from '@/shared/ui/navSections';
import { activeRail, panelAt, railDef } from '@/shared/ui/railSections';
import { PinnedPagesProvider } from '@/shared/ui/PinnedPagesProvider';
import { usePinnedPages } from '@/shared/ui/pinnedPagesContext';
import { pinnedGroup } from '@/shared/ui/pinnedSections';
import { workNav, settingsNav } from '@/shared/ui/navConfig';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { Icon } from './Icon';
import { Close, Help, MarkEmailUnread, Search } from './icons';
import { IconButton } from './Button';

/**
 * Подпанель раздела скрыта (issue #1429; ключ — со времён свёртки панели в иконки, #1355). Выбор —
 * настройка человека, как тема: лежит на сервере и едет за ним на другую машину, а зеркало в браузере
 * даёт первый кадр без прыжка ширины.
 *
 * Пока человек не выбирал, решает ширина окна (`auto`): ниже ~1200 px рейл 80 и подпанель 240
 * оставили бы странице меньше 900 px. `auto` на сервер не уходит — отправляет только нажатие.
 */
const RAIL_KEY = 'rail';
const RAIL_MIRROR = 'crg-rail';
const ROOMY = '(min-width: 1200px)';

/**
 * Оболочка приложения. Рабочее пространство (issue #1001) поставляется ВОКРУГ неё: шапка, меню и
 * страницы под ней читают одно и то же открытое пространство.
 */
export function AppShell() {
  return (
    <WorkspaceProvider>
      <SiteFilterProvider>
        <PinnedPagesProvider>
          <Shell />
        </PinnedPagesProvider>
      </SiteFilterProvider>
    </WorkspaceProvider>
  );
}

function Shell() {
  const { allWork, work, outside, settings, rail: sections } = useNavSections();
  const { pathname } = useLocation();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [rail, setRail] = useSyncedPreference(RAIL_KEY, RAIL_MIRROR, 'auto');
  const roomy = useMedia(ROOMY);
  const panelWanted = rail === 'auto' ? roomy : rail !== 'collapsed';
  // Раздел адреса — среди ВСЕХ видных пунктов: страница вне пространства тоже принадлежит разделу.
  const active = activeRail(pathname, [...allWork, ...settings]);
  const activeDef = active ? railDef(active) : null;
  const panelSection = activeDef && panelAt(pathname, activeDef) ? activeDef : null;
  // Страница может подавить подпанель (открытый счёт, D-10.5): выбор человека тогда не пишется, а «Показать
  // панель» открывает её поверх, drawer'ом.
  const panel = useSectionPanel({ has: !!panelSection, wanted: panelWanted, expand: () => setRail('expanded') });
  const panelShown = panel.shown;
  const favoriteSites = useFavoriteSitesGroup();
  // Избранные стройки — быстрым переходом к карточке (#1432): шапка их больше не показывает.
  const panelGroups = panelSection
    ? withFavoriteSites(panelSection.id, sections.find(s => s.def.id === panelSection.id)?.groups ?? [], favoriteSites)
    : [];
  // Закреплённые страницы (work-04, макет Р0): из ВСЕХ видных пунктов — страница вне пространства в
  // группе есть, с пометкой. Группа — у рабочих разделов; на телефоне — место «Закреплённые» нижней
  // строки (work-03), а не группа меню страниц заголовка.
  const { list: pinnedList } = usePinnedPages();
  const { inside } = useWorkspace();
  const pinned = pinnedGroup(pinnedList, [...allWork, ...settings], inside);
  // Телефон (issue #1434, work-03): рейла нет — внизу «Главная · Закреплённые · Меню», страницы раздела —
  // в меню заголовка страницы, «Администрирование» — пункт меню профиля.
  const wide = useMedia(WIDE);
  const { sectionMenu, admin, bottomNav } = phoneChrome({ wide, panelSection, panelGroups, outside, sections });
  // Основное действие экрана — плавающей кнопкой на телефоне; пока карточка закрывает список, её нет.
  const { data: access } = useAccess();
  const fabState = usePrimaryFabState();
  const fab = !wide && !fabState.covered && access ? primaryActionAt(pathname, access) : undefined;
  // Уведомления встают правее рейла и над тем, что стоит у нижнего края. Их поле — вне оболочки,
  // поэтому место — переменными на корне документа, и ставит их одно место, которое знает и рейл, и
  // строку, и кнопку.
  const railRef = useRef<HTMLElement>(null);
  // Палитра встаёт на место строки поиска (docked search view MD3) — ей нужен её прямоугольник.
  const searchRef = useRef<HTMLButtonElement>(null);
  useSnackbarPlace(railRef, snackbarLift({ bottomNav: bottomNav.length > 0, fab: !!fab }));

  // Глобальные шорткаты (issue #107): Ctrl/⌘+K — палитра, «?» — справка (не в полях ввода).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        // Поверх окна, которое сейчас нельзя закрыть (правки, запрос, результат), палитра не
        // открывается (issue #1238): её команда увела бы со страницы вместе с окном. Молчать при
        // этом нельзя — верхнее окно показывает свой выход, как на промах мимо него.
        // Открытую палитру Ctrl+K закрывает всегда: окно под ней могло стать держащим, пока она
        // открыта, и запрет ниже не должен запирать её саму.
        if (paletteShown()) { setPaletteOpen(false); return; }
        if (refusedByDialog()) return;
        setPaletteOpen(true);
        return;
      }
      if (e.key === '?' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        const el = document.activeElement as HTMLElement | null;
        const editable = !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
        if (!editable) { e.preventDefault(); setHelpOpen(o => !o); }
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    // Высота — видимая область (`dvh`), а не 100vh: на телефоне 100vh выше экрана на панель браузера,
    // и нижняя навигация уходила бы под неё (#1434). Браузеру без `dvh` остаётся 100vh, а не ничего.
    <div className="flex h-screen supports-[height:100dvh]:h-dvh bg-surface-container-low">
      {/* Рейл разделов и подпанель раздела — на рабочем столе (issue #1429). */}
      <aside ref={railRef} data-testid="rail" data-panel={panelShown || undefined}
        className="shrink-0 hidden md:flex">
        <SectionRail sections={sections} active={active} panelHidden={!!panelSection && !panelShown}
          onShowPanel={panel.show} />
        {panelShown && panelSection && (
          <SectionPanel def={panelSection} outside={outside} onHide={() => setRail('collapsed')} groups={panelGroups}
            pinned={pinned} pinnable={isNavPage} />
        )}
      </aside>
      {panelSection && (
        <SectionPanelDrawer open={panel.drawer} onClose={panel.closeDrawer} title={panelSection.title}>
          {/* «Скрыть» в drawer закрывает его и только: выбор человека подавленная подпанель не меняет. */}
          <SectionPanel def={panelSection} outside={outside} onHide={panel.closeDrawer} groups={panelGroups}
            pinned={pinned} pinnable={isNavPage} />
        </SectionPanelDrawer>
      )}

      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} anchorRef={searchRef} />
      <ShortcutsHelp open={helpOpen} onOpenChange={setHelpOpen} />

      {/* Content */}
      <main className="flex-1 min-w-0 flex flex-col overflow-hidden">
        {/* Шапка (issue #1430): пространство, поиск, активность (#1431), справка, профиль. Одной
            строкой и на телефоне: вторая строка (#1355) была нужна подписи избранного, а оно ушло в
            отбор над списками (#1432). Короткое имя пространства ужимается вместе с поиском.
            Гамбургера на телефоне нет (#1434): разделы — в нижней навигации, страницы раздела — в
            меню заголовка страницы. Top app bar MD3 (sharedui-md3-15): 64, без черты. На рабочем
            столе — прозрачная, на поверхности рейла; от содержимого её отделяет цвет и угол рабочей
            области. На телефоне полоса и содержимое — одна поверхность `surface`: знак экземпляра 48,
            строка поиска 56, аватар 48 — цели касания 48 видны глазом ([UI-68]). */}
        <header className="h-16 shrink-0 max-md:bg-surface flex items-center gap-1 px-2 md:px-4">
          <PhoneBrand />
          {/* Скрытая подпанель возвращается отсюда — с края страницы, где она стояла. */}
          {panelSection && !panelShown && <span className="hidden md:inline-flex"><ShowPanelButton onShow={panel.show} /></span>}
          {/* Переключатель пространства — слева: это контекст экрана, а не действие (AUTH-16.1). Без
              обёртки: при одном модуле его нет, и обёртка пустой строкой ложилась бы под шапку. */}
          <WorkspaceSwitcher />
          <SearchButton ref={searchRef} onOpen={() => setPaletteOpen(true)} />
          <ActivityCenter />
          {/* Видимый вход в справку: клавишу «?» знает только тот, кто её уже нашёл. На телефоне
              справки по клавишам нет — клавиатуры там нет, а место в строке нужно аватару. */}
          <IconButton label="Справка и горячие клавиши" className="max-md:hidden" onClick={() => setHelpOpen(true)} icon={Help} />
          <span className="pl-1"><AccountMenu admin={admin} /></span>
        </header>
        {/* Рабочая область — своя поверхность `surface` со скруглённым ведущим верхним углом 16 (на
            рабочем столе). Обёртка — вокруг прокрутки, а не внутри неё: иначе угол уезжал бы с
            прокруткой, а обёртка стала бы контейнером прокрутки для липких элементов страницы. Без
            `transform`, `contain` и `will-change` — иначе поедет `fixed` внутри. Плашки оболочки лежат
            в ней. `header` — прямой потомок `main`: на этом держатся живые проверки. */}
        <div data-testid="work-area" className="flex-1 min-h-0 flex flex-col bg-surface md:rounded-tl-lg overflow-clip">
        <OutsideBanner items={allWork} inside={work} />
        <EmailConfirmBanner />
        {/* Плавающая кнопка стоит над содержимым, а не в нём: прокрутка списка её не уносит. Пока
            она есть, `--fab-clearance` говорит спискам, сколько места оставить под концом, чтобы
            последняя строка поднималась выше кнопки (`ListDetailShell`, список строек). */}
        <div className="relative flex-1 min-h-0 flex flex-col"
          style={fab ? { '--fab-clearance': FAB_CLEARANCE } as CSSProperties : undefined}>
          <div className="flex-1 overflow-auto">
            <SectionMenuContext.Provider value={sectionMenu}>
              <PrimaryFabContext.Provider value={fabState.report}>
                <SuppressSectionPanelContext.Provider value={panel.suppress}>
                  <Outlet />
                </SuppressSectionPanelContext.Provider>
              </PrimaryFabContext.Provider>
            </SectionMenuContext.Provider>
          </div>
          {fab && <PrimaryFab action={fab} busy={fabState.isBusy(fab.id)} />}
        </div>
        </div>
        <BottomNav sections={bottomNav} active={active} pinned={pinned} pinnable={isNavPage} />
      </main>
    </div>
  );
}

/** Страница навигации — её можно закрепить; избранная стройка в подпанели — запись, а не страница. */
const NAV_PAGES = new Set([...workNav, ...settingsNav].map(n => n.to));
const isNavPage = (to: string) => NAV_PAGES.has(to);

/**
 * Знак экземпляра на телефоне (ТЗ CORE-25.1, #1434): рейла с логотипом там нет, и без этой кнопки
 * оформление компании не было бы видно ни на одном экране. Название — в имени ссылки: в строку
 * рядом с поиском и аватаром оно не помещается.
 */
function PhoneBrand() {
  const productName = useProductName();
  const guardedClick = useGuardedNavClick();
  return (
    <Link to="/home" onClick={guardedClick('/home')} aria-label={`${productName} — на главную`} data-testid="phone-brand"
      className="md:hidden shrink-0 flex items-center justify-center w-12 h-12 rounded-full state-layer focus-ring">
      <BrandLogo className="w-7 h-7 text-primary" iconSize={22} />
    </Link>
  );
}

/**
 * Поиск в шапке (issue #1430) — видимая дверь к палитре Ctrl K: клавишу знает только тот, кто её уже
 * нашёл. По образцу MD3 search bar: 56, форма full, `surface-container-high`. Занимает свободное место
 * строки, но не шире 720 px — дальше строка поиска читается как разделитель, а не поле. Что палитра
 * ищет — #1433.
 */
function SearchButton({ onOpen, ref }: { onOpen: () => void; ref: Ref<HTMLButtonElement> }) {
  // На Mac палитру зовут ⌘K (слушатель принимает и Ctrl, и ⌘), и подсказка называет ту клавишу,
  // которой там пользуются.
  const mac = /Mac|iPhone|iPad/.test(navigator.platform);
  return (
    // Без обёртки: кнопка — сама элемент полосы, и её `min-w-14` держит строку сама. Обёртка с
    // `min-w-0` уходила на 375 px в 0, и кнопка вылезала из неё поверх центра активности; ужимается
    // переключатель пространства — его имя обрезается. Остаток строки сверх 720 забирает `me-auto`,
    // и центр активности с аватаром стоят у края.
    // Узкое место (контекст экрана забрал строку) — по ширине самой кнопки: сначала обрезается
    // подпись, потом уходит метка клавиш, потом подпись становится невидимой, и остаётся значок
    // поиска. Метка держится дольше подписи: учить клавише — смысл кнопки.
    <button ref={ref} type="button" onClick={onOpen} aria-keyshortcuts={mac ? 'Meta+K' : 'Control+K'} data-testid="header-search"
      className="@container flex-1 min-w-14 max-w-180 flex items-center gap-4 h-14 px-4 ms-1 md:ms-3 me-auto overflow-hidden rounded-full bg-surface-container-high text-body-large text-on-surface-variant state-layer focus-ring">
      <Icon icon={Search} size={24} className="shrink-0" />
      <span className="flex-1 min-w-0 truncate text-left @max-[8rem]:sr-only">Найти счёт, стройку или действие</span>
      <kbd className="hidden @[14rem]:inline shrink-0 rounded-xs border border-outline-variant px-1.5 text-label-small text-on-surface-variant">{mac ? '⌘ K' : 'Ctrl K'}</kbd>
    </button>
  );
}

/** Мягкий баннер «подтвердите email» (issue #148) — не гейтит вход, закрывается на сессию. */
function EmailConfirmBanner() {
  const { data: account } = useAccount();
  const resend = useResendConfirmation();
  const [dismissed, setDismissed] = useState(false);
  const [sent, setSent] = useState(false);
  const guardedClick = useGuardedNavClick();

  if (dismissed || !account || account.emailConfirmed) return null;

  return (
    <div className="shrink-0 flex items-center gap-2 px-4 py-2 text-body-medium bg-warning-container text-on-warning-container border-b border-warning/30">
      <Icon icon={MarkEmailUnread} size={18} className="shrink-0" />
      <span className="flex-1">
        Email <span className="font-medium">{account.email}</span> не подтверждён.{' '}
        {sent
          ? <span className="text-on-surface-variant">Письмо отправлено — проверьте почту.</span>
          : <button type="button" onClick={() => resend.mutate(undefined, { onSuccess: () => setSent(true) })}
              disabled={resend.isPending}
              className="font-medium underline hover:no-underline focus-ring disabled-content">
              Отправить письмо
            </button>}
        {' · '}
        <Link to="/profile" onClick={guardedClick('/profile')} className="font-medium underline hover:no-underline">Профиль</Link>
      </span>
      {/* Знак — парой контейнера (`on-warning-container`), а не `on-surface-variant` кнопки; `-my-2` держит
          высоту баннера прежней: кнопка 40 иначе раздвинула бы строку на 16 px. */}
      <IconButton label="Скрыть" icon={Close} iconClassName="text-on-warning-container" className="-my-2"
        onClick={() => setDismissed(true)} />
    </div>
  );
}
