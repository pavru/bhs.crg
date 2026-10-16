import { useState, useEffect } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Link, Outlet } from 'react-router';
import { useAccount, useResendConfirmation } from '@/shared/api/account';
import { NotificationsCenter } from '@/features/notifications/NotificationsCenter';
import { ActiveJobsIndicator } from '@/features/jobs/ActiveJobsIndicator';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { CommandPalette } from '@/shared/ui/CommandPalette';
import { ShortcutsHelp } from '@/shared/ui/ShortcutsHelp';
import { workNav } from '@/shared/ui/navConfig';
import { visibleNav } from '@/shared/ui/navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { paletteShown, refusedByDialog } from '@/shared/ui/modalDismissal';
import { WorkspaceProvider } from '@/shared/ui/WorkspaceProvider';
import { WorkspaceSwitcher } from '@/shared/ui/WorkspaceSwitcher';
import { OutsideBanner } from '@/shared/ui/WorkspaceNav';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { splitNav } from '@/shared/ui/workspaces';
import { SiteFilterProvider } from '@/shared/siteFilter/SiteFilterProvider';
import { SiteFilterSwitch } from '@/shared/siteFilter/SiteFilterSwitch';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import { RailBody } from '@/shared/ui/AppRail';
import { MailWarning, X, PanelLeftClose, PanelLeftOpen, Menu } from 'lucide-react';

/**
 * Боковая панель свёрнута в иконки (issue #1355). Выбор — настройка человека, как тема: лежит на
 * сервере и едет за ним на другую машину, а зеркало в браузере даёт первый кадр без прыжка ширины.
 */
const RAIL_KEY = 'rail';
const RAIL_MIRROR = 'crg-rail';

/**
 * Узкий экран (issue #1355): ниже `md` панели у края нет, меню выдвигается кнопкой в шапке. Порог
 * записан и здесь, и классами `md:` ниже — меню закрывается само, когда окно расширили, иначе оно
 * лежало бы поверх панели, вернувшейся на место.
 */
const WIDE = '(min-width: 768px)';

const iconButton = 'shrink-0 p-2 rounded-full text-fg3 hover:bg-black/5 dark:hover:bg-white/10 ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand';

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
  const productName = useProductName();
  const { data: access } = useAccess();
  const workspace = useWorkspace();
  const allWork = visibleNav(workNav, access ?? NO_ACCESS);
  const { inside: work } = splitNav(allWork, workspace.current, access ?? NO_ACCESS);
  const [pwOpen, setPwOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [rail, setRail] = useSyncedPreference(RAIL_KEY, RAIL_MIRROR, 'expanded');
  const collapsed = rail === 'collapsed';
  const railToggle = collapsed ? 'Развернуть панель' : 'Свернуть панель';

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

  useEffect(() => {
    const wide = window.matchMedia(WIDE);
    const onChange = () => { if (wide.matches) setMenuOpen(false); };
    wide.addEventListener('change', onChange);
    return () => wide.removeEventListener('change', onChange);
  }, []);

  const brand = (
    <>
      <BrandLogo className="w-6 h-6 text-brand" iconSize={18} />
      <span className="flex-1 min-w-0 text-base font-semibold tracking-tight text-brand truncate">
        {productName}
      </span>
    </>
  );

  return (
    <div className="flex h-screen bg-base">
      {/* Navigation rail — на рабочем столе. */}
      <aside data-testid="rail" data-collapsed={collapsed || undefined}
        className={`${collapsed ? 'w-16' : 'w-56'} shrink-0 hidden md:flex flex-col border-r border-stroke bg-surface`}>
        <RailBody collapsed={collapsed} navId="app-rail-nav" onChangePassword={() => setPwOpen(true)} head={
          // Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. В свёрнутой
          // панели от строки остаётся кнопка разворота: логотип без названия ничего не называет.
          <div className={`flex items-center gap-2 h-12 border-b border-stroke shrink-0 ${collapsed ? 'justify-center' : 'pl-4 pr-2'}`}>
            {!collapsed && brand}
            <button type="button" onClick={() => setRail(collapsed ? 'expanded' : 'collapsed')}
              aria-label={railToggle} title={railToggle} aria-expanded={!collapsed} aria-controls="app-rail-nav"
              className={iconButton}>
              {collapsed ? <PanelLeftOpen size={18} /> : <PanelLeftClose size={18} />}
            </button>
          </div>
        } />
      </aside>

      {/* Выдвижное меню — на узком экране (issue #1355). Та же панель, всегда развёрнутая: свёртка
          в иконки нужна, чтобы отдать ширину странице, а выдвижному меню отдавать нечего. */}
      <Dialog.Root open={menuOpen} onOpenChange={setMenuOpen}>
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40 md:hidden" />
          <Dialog.Content data-modal-state="free" data-testid="rail-drawer" aria-describedby={undefined}
            className="fixed inset-y-0 left-0 z-50 w-72 max-w-[85vw] flex flex-col bg-surface border-r border-stroke focus:outline-none md:hidden"
            style={{ boxShadow: 'var(--f-shadow28)' }}>
            <Dialog.Title className="sr-only">Меню</Dialog.Title>
            <RailBody collapsed={false} navId="app-drawer-nav" onChangePassword={() => setPwOpen(true)}
              onPick={() => setMenuOpen(false)} head={
                <div className="flex items-center gap-2 h-12 pl-4 pr-2 border-b border-stroke shrink-0">
                  {brand}
                  <Dialog.Close aria-label="Закрыть меню" title="Закрыть меню" className={iconButton}>
                    <X size={18} />
                  </Dialog.Close>
                </div>
              } />
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <ShortcutsHelp open={helpOpen} onOpenChange={setHelpOpen} />

      {/* Content */}
      <main className="flex-1 min-w-0 flex flex-col overflow-hidden">
        {/* На узком экране шапка — две строки (issue #1355): сверху меню, задачи и уведомления, под
            ними контекст экрана. В одну строку 390 px не вмещают: подпись избранного ложилась в два
            этажа поверх колокольчика. Прятать контекст в «ещё» нельзя — он говорит, что видно. */}
        <header className="min-h-12 md:h-12 shrink-0 border-b border-stroke bg-surface flex flex-wrap md:flex-nowrap items-center gap-1 px-2 md:px-4">
          <button type="button" onClick={() => setMenuOpen(true)} aria-label="Меню" title="Меню"
            className={`${iconButton} md:hidden h-12`}>
            <Menu size={20} />
          </button>
          <div data-testid="header-context"
            className="order-last md:order-none w-full md:w-auto min-w-0 flex items-center gap-1 whitespace-nowrap max-md:[&>*]:shrink-0 max-md:overflow-x-auto max-md:pb-1.5">
            {/* Переключатель пространства — слева: это контекст экрана, а не действие (AUTH-16.1). */}
            <WorkspaceSwitcher />
            {/* Избранные стройки — за пространством (#1218): тоже «что я вижу», а не действие. */}
            <SiteFilterSwitch />
          </div>
          <div className="flex-1" />
          <ActiveJobsIndicator />
          <NotificationsCenter />
        </header>
        <OutsideBanner items={allWork} inside={work} />
        <EmailConfirmBanner />
        <div className="flex-1 overflow-auto">
          <Outlet />
        </div>
      </main>
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
