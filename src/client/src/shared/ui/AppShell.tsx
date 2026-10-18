import { useState, useEffect } from 'react';
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
import { PrimaryFabCoverContext } from '@/shared/ui/primaryFabContext';
import { primaryActionAt } from '@/shared/ui/paletteActions';
import { SectionPanel, SectionRail, ShowPanelButton } from '@/shared/ui/SectionRail';
import { useFavoriteSitesGroup, withFavoriteSites } from '@/shared/siteFilter/favoriteSitesGroup';
import { useNavSections } from '@/shared/ui/navSections';
import { activeRail, panelAt, railDef } from '@/shared/ui/railSections';
import { CircleHelp, MailWarning, X, Search } from 'lucide-react';
import { IconButton } from './Button';

/**
 * Подпанель раздела скрыта (issue #1429; ключ — со времён свёртки панели в иконки, #1355). Выбор —
 * настройка человека, как тема: лежит на сервере и едет за ним на другую машину, а зеркало в браузере
 * даёт первый кадр без прыжка ширины.
 *
 * Пока человек не выбирал, решает ширина окна (`auto`): ниже ~1200 px рейл 88 и подпанель 240
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
        <Shell />
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
  const panelShown = !!panelSection && panelWanted;
  const favoriteSites = useFavoriteSitesGroup();
  // Избранные стройки — быстрым переходом к карточке (#1432): шапка их больше не показывает.
  const panelGroups = panelSection
    ? withFavoriteSites(panelSection.id, sections.find(s => s.def.id === panelSection.id)?.groups ?? [], favoriteSites)
    : [];
  // Телефон (issue #1434): рейла нет — разделы внизу, страницы раздела — в меню заголовка страницы,
  // «Администрирование» — пункт меню профиля.
  const wide = useMedia(WIDE);
  const { sectionMenu, admin } = phoneChrome({ wide, panelSection, panelGroups, outside, sections });
  // Основное действие экрана — плавающей кнопкой на телефоне; пока карточка закрывает список, её нет.
  const { data: access } = useAccess();
  const [fabCovered, setFabCovered] = useState(false);
  const fab = !wide && !fabCovered && access ? primaryActionAt(pathname, access) : undefined;

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
    <div className="flex h-screen supports-[height:100dvh]:h-dvh bg-base">
      {/* Рейл разделов и подпанель раздела — на рабочем столе (issue #1429). */}
      <aside data-testid="rail" data-panel={panelShown || undefined}
        className="shrink-0 hidden md:flex border-r border-stroke">
        <SectionRail sections={sections} active={active} panelHidden={!!panelSection && !panelShown}
          onShowPanel={() => setRail('expanded')} />
        {panelShown && (
          <SectionPanel def={panelSection} outside={outside} onHide={() => setRail('collapsed')} groups={panelGroups} />
        )}
      </aside>

      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <ShortcutsHelp open={helpOpen} onOpenChange={setHelpOpen} />

      {/* Content */}
      <main className="flex-1 min-w-0 flex flex-col overflow-hidden">
        {/* Шапка (issue #1430): пространство, поиск, активность (#1431), справка, профиль. Одной
            строкой и на телефоне: вторая строка (#1355) была нужна подписи избранного, а оно ушло в
            отбор над списками (#1432). Короткое имя пространства ужимается вместе с поиском.
            Гамбургера на телефоне нет (#1434): разделы — в нижней навигации, страницы раздела — в
            меню заголовка страницы. Цели касания там — 44 px (`max-md:h-11`). */}
        <header className="h-12 shrink-0 border-b border-stroke bg-surface flex items-center gap-1 px-2 md:px-4">
          <PhoneBrand />
          {/* Скрытая подпанель возвращается отсюда — с края страницы, где она стояла. */}
          {panelSection && !panelShown && <span className="hidden md:inline-flex"><ShowPanelButton onShow={() => setRail('expanded')} /></span>}
          {/* Переключатель пространства — слева: это контекст экрана, а не действие (AUTH-16.1). Без
              обёртки: при одном модуле его нет, и обёртка пустой строкой ложилась бы под шапку. */}
          <WorkspaceSwitcher />
          <SearchButton onOpen={() => setPaletteOpen(true)} />
          <ActivityCenter />
          {/* Видимый вход в справку: клавишу «?» знает только тот, кто её уже нашёл. На телефоне
              справки по клавишам нет — клавиатуры там нет, а место в строке нужно аватару. */}
          <IconButton label="Справка и горячие клавиши" size="lg" className="max-md:hidden" onClick={() => setHelpOpen(true)}>
            <CircleHelp size={20} />
          </IconButton>
          <span className="pl-1"><AccountMenu admin={admin} /></span>
        </header>
        <OutsideBanner items={allWork} inside={work} />
        <EmailConfirmBanner />
        {/* Плавающая кнопка стоит над содержимым, а не в нём: прокрутка списка её не уносит. */}
        <div className="relative flex-1 min-h-0 flex flex-col">
          <div className="flex-1 overflow-auto">
            <SectionMenuContext.Provider value={sectionMenu}>
              <PrimaryFabCoverContext.Provider value={setFabCovered}>
                <Outlet />
              </PrimaryFabCoverContext.Provider>
            </SectionMenuContext.Provider>
          </div>
          {fab && <PrimaryFab action={fab} />}
        </div>
        <BottomNav sections={sections} active={active} />
      </main>
    </div>
  );
}

/**
 * Знак экземпляра на телефоне (ТЗ CORE-25.1, #1434): рейла с логотипом там нет, и без этой кнопки
 * оформление компании не было бы видно ни на одном экране. Название — в имени ссылки: в строку
 * рядом с поиском и аватаром оно не помещается.
 */
function PhoneBrand() {
  const productName = useProductName();
  const guardedClick = useGuardedNavClick();
  return (
    <Link to="/" onClick={guardedClick('/')} aria-label={`${productName} — на главную`} data-testid="phone-brand"
      className="md:hidden shrink-0 flex items-center justify-center w-11 h-11 rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
      <BrandLogo className="w-7 h-7 text-brand" iconSize={22} />
    </Link>
  );
}

/**
 * Поиск в шапке (issue #1430) — видимая дверь к палитре Ctrl K: клавишу знает только тот, кто её уже
 * нашёл. Занимает свободное место строки, но не шире 640 px — дальше строка поиска читается как
 * разделитель, а не поле. Что палитра ищет — #1433.
 */
function SearchButton({ onOpen }: { onOpen: () => void }) {
  // На Mac палитру зовут ⌘K (слушатель принимает и Ctrl, и ⌘), и подсказка называет ту клавишу,
  // которой там пользуются.
  const mac = /Mac|iPhone|iPad/.test(navigator.platform);
  return (
    <div className="flex-1 min-w-0 flex">
      {/* Узкое место (контекст экрана забрал строку) — по ширине самой кнопки: сначала
          обрезается подпись, потом уходит метка клавиш, потом подпись становится невидимой, и
          остаётся значок поиска. Метка держится дольше подписи: учить клавише — смысл кнопки. */}
      <button type="button" onClick={onOpen} aria-keyshortcuts={mac ? 'Meta+K' : 'Control+K'}
        className="@container flex-1 min-w-10 max-md:min-w-11 max-w-160 flex items-center gap-2.5 h-10 max-md:h-11 px-2.5 mx-1 md:mx-3 overflow-hidden rounded-full bg-base text-sm text-fg3 hover:bg-black/5 dark:hover:bg-white/10 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
        <Search size={18} className="shrink-0" />
        <span className="flex-1 min-w-0 truncate text-left @max-[8rem]:sr-only">Найти счёт, стройку или действие</span>
        <kbd className="hidden @[14rem]:inline shrink-0 rounded border border-stroke px-1.5 text-caption text-fg4">{mac ? '⌘ K' : 'Ctrl K'}</kbd>
      </button>
    </div>
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
    <div className="shrink-0 flex items-center gap-2 px-4 py-2 text-sm bg-warning-subtle text-warning border-b border-warning/30">
      <MailWarning size={16} className="shrink-0" />
      <span className="flex-1">
        Email <span className="font-medium">{account.email}</span> не подтверждён.{' '}
        {sent
          ? <span className="text-fg2">Письмо отправлено — проверьте почту.</span>
          : <button type="button" onClick={() => resend.mutate(undefined, { onSuccess: () => setSent(true) })}
              disabled={resend.isPending}
              className="font-medium underline hover:no-underline disabled:opacity-50">
              Отправить письмо
            </button>}
        {' · '}
        <Link to="/profile" onClick={guardedClick('/profile')} className="font-medium underline hover:no-underline">Профиль</Link>
      </span>
      <button type="button" onClick={() => setDismissed(true)} aria-label="Скрыть"
        className="shrink-0 p-1 rounded hover:bg-warning/10">
        <X size={14} />
      </button>
    </div>
  );
}
