import { formatDateTime } from '@/shared/format/format';
import { useState, useEffect } from 'react';
import { NavLink, Link, Outlet } from 'react-router';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useAccount, useResendConfirmation } from '@/shared/api/account';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { NotificationsCenter } from '@/features/notifications/NotificationsCenter';
import { ActiveJobsIndicator } from '@/features/jobs/ActiveJobsIndicator';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { Avatar } from '@/shared/ui/Avatar';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { CommandPalette } from '@/shared/ui/CommandPalette';
import { ShortcutsHelp } from '@/shared/ui/ShortcutsHelp';
import { workNav, settingsNav, type NavItem } from '@/shared/ui/navConfig';
import { visibleNav } from '@/shared/ui/navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useGuardedLeave, useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { paletteShown, refusedByDialog } from '@/shared/ui/modalDismissal';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { WorkspaceProvider } from '@/shared/ui/WorkspaceProvider';
import { WorkspaceSwitcher } from '@/shared/ui/WorkspaceSwitcher';
import { OutsideBanner, OutsideNav } from '@/shared/ui/WorkspaceNav';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { splitNav } from '@/shared/ui/workspaces';
import { SiteFilterProvider } from '@/shared/siteFilter/SiteFilterProvider';
import { SiteFilterSwitch } from '@/shared/siteFilter/SiteFilterSwitch';
import { useSyncedPreference } from '@/shared/hooks/useSyncedPreference';
import {
  LogOut, KeyRound, ChevronsUpDown, MailWarning, X, Bug, PanelLeftClose, PanelLeftOpen, type LucideIcon,
} from 'lucide-react';

/**
 * Боковая панель свёрнута в иконки (issue #1355). Выбор — настройка человека, как тема: лежит на
 * сервере и едет за ним на другую машину, а зеркало в браузере даёт первый кадр без прыжка ширины.
 */
const RAIL_KEY = 'rail';
const RAIL_MIRROR = 'crg-rail';

function NavSection({
  label,
  items,
  collapsed,
}: {
  label: string;
  items: NavItem[];
  collapsed: boolean;
}) {
  const guardedClick = useGuardedNavClick();
  return (
    <div>
      {/* Заголовок группы в свёрнутой панели не помещается: группы там делит черта. */}
      <p className={collapsed ? 'sr-only'
        : 'px-3 mb-1 text-caption font-semibold uppercase tracking-widest select-none text-fg4'}>
        {label}
      </p>
      <div className="space-y-0.5">
        {items.map(({ to, label: itemLabel, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
            onClick={guardedClick(to)}
            // Подсказка — только у иконки: в развёрнутой панели она повторила бы видимую подпись.
            title={collapsed ? itemLabel : undefined}
            className={({ isActive }) =>
              `group flex items-center rounded-full text-sm font-medium select-none transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
                collapsed ? 'justify-center w-10 h-10 mx-auto' : 'gap-3 px-3 py-2.5'} ${
                isActive
                  ? 'bg-tonal text-on-tonal'
                  : 'text-fg1 hover:bg-black/5 dark:hover:bg-white/10'
              }`
            }
          >
            {({ isActive }) => (
              <>
                <Icon
                  size={18}
                  className={`shrink-0 ${isActive ? 'text-on-tonal' : 'text-fg3'}`}
                />
                <span className={collapsed ? 'sr-only' : undefined}>{itemLabel}</span>
              </>
            )}
          </NavLink>
        ))}
      </div>
    </div>
  );
}

/** Кнопка низа панели: в свёрнутой — иконка с подсказкой, подпись остаётся для диктора. */
function RailButton({ icon: Icon, label, onClick, collapsed }: {
  icon: LucideIcon; label: string; onClick: () => void; collapsed: boolean;
}) {
  return (
    <button type="button" onClick={onClick} title={collapsed ? label : undefined}
      className={`flex items-center gap-3 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 ` +
        `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
        collapsed ? 'justify-center w-10 h-10 mx-auto' : 'w-full h-14 px-4'}`}>
      <Icon size={collapsed ? 20 : 22} className="text-fg3 shrink-0" />
      <span className={collapsed ? 'sr-only' : 'text-sm font-medium text-fg1'}>{label}</span>
    </button>
  );
}

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
  const { user, logout } = useAuth();
  const { data: account } = useAccount();
  const productName = useProductName();
  // Навигация строится ТОЛЬКО по ответу /api/account/access (ТЗ AUTH-14). Роли здесь больше нет:
  // она приходила из токена, то есть устаревала ровно тогда, когда это опаснее всего — при снятии
  // прав, — и показывала разделы, которые отвечают отказом (issue #952).
  const { data: access } = useAccess();
  const allWork = visibleNav(workNav, access ?? NO_ACCESS);
  const settings = visibleNav(settingsNav, access ?? NO_ACCESS);
  // Пространство сужает только рабочие разделы (ТЗ AUTH-16.1, issue #1001). «Настройка системы» —
  // администрирование, а не контекст работы: прятать «Пользователей» за пространством «Счета» значило
  // бы заставлять администратора переключаться ради своего же меню. Скрытое не теряется — оно в
  // строке «Ещё N разделов вне пространства» и в палитре команд.
  const workspace = useWorkspace();
  // Делит то же правило, что проверяют тесты (`splitNav`), а не его копия здесь (ревью PR #1330).
  const { inside: work, outside } = splitNav(allWork, workspace.current, access ?? NO_ACCESS);
  const hasWork = work.length > 0 || outside.length > 0;
  const [pwOpen, setPwOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const bugReport = useBugReportDialog();
  const guardedProfileClick = useGuardedNavClick();
  const guardedLeave = useGuardedLeave();
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

  return (
    <div className="flex h-screen bg-base">
      {/* Navigation rail */}
      <aside data-testid="rail" data-collapsed={collapsed || undefined}
        className={`${collapsed ? 'w-16' : 'w-56'} shrink-0 flex flex-col border-r border-stroke bg-surface`}>
        {/* Brand */}
        {/* Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. В свёрнутой
            панели от строки остаётся кнопка разворота: логотип без названия ничего не называет. */}
        <div className={`flex items-center gap-2 h-12 border-b border-stroke shrink-0 ${collapsed ? 'justify-center' : 'pl-4 pr-2'}`}>
          {!collapsed && <>
            <BrandLogo className="w-6 h-6 text-brand" iconSize={18} />
            <span className="flex-1 min-w-0 text-base font-semibold tracking-tight text-brand truncate">
              {productName}
            </span>
          </>}
          <button type="button" onClick={() => setRail(collapsed ? 'expanded' : 'collapsed')}
            aria-label={railToggle} title={railToggle} aria-expanded={!collapsed} aria-controls="app-rail-nav"
            className="shrink-0 p-2 rounded-full text-fg3 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            {collapsed ? <PanelLeftOpen size={18} /> : <PanelLeftClose size={18} />}
          </button>
        </div>

        {/* Nav sections */}
        <nav id="app-rail-nav" className={`flex-1 py-3 space-y-4 overflow-y-auto ${collapsed ? 'px-3' : 'px-2'}`}>
          {/* Группа есть, пока в ней есть хоть что-то — пункты пространства ИЛИ строка скрытых (ревью
              PR #1330): иначе «Ещё N разделов» стояла бы без заголовка и без черты перед настройкой. */}
          {hasWork && (
            <div>
              <NavSection label="Документы и данные" items={work} collapsed={collapsed} />
              <OutsideNav items={outside} collapsed={collapsed} />
            </div>
          )}
          {settings.length > 0 && (
            <>
              {hasWork && <div className="border-t border-stroke mt-2" />}
              <NavSection label="Настройка системы" items={settings} collapsed={collapsed} />
            </>
          )}
        </nav>

        {/* Bottom: theme + user (MD3 drawer footer, issue #157) */}
        <div className="px-3 pt-3 pb-1 border-t border-stroke shrink-0">
          <div className={collapsed ? 'mb-3 flex justify-center' : 'mb-3'}><ThemeToggle vertical={collapsed} /></div>

          <div className="space-y-0.5">
            {/* Блок пользователя — пилюля с аватаром; в свёрнутой панели — один аватар. */}
            <NavLink to="/profile" onClick={guardedProfileClick('/profile')}
              title={collapsed ? (user?.displayName || user?.email) : undefined}
              className={({ isActive }) => `flex items-center gap-3 rounded-[28px] transition-colors ` +
                `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ` +
                (collapsed ? 'justify-center w-12 h-12 mx-auto ' : 'h-14 px-4 ') +
                (isActive ? 'bg-tonal' : 'hover:bg-black/5 dark:hover:bg-white/10')}>
              <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} />

              <span className={collapsed ? 'sr-only' : 'flex-1 min-w-0'}>
                <span className="block text-sm font-medium text-fg1 truncate">{user?.displayName || user?.email}</span>
                {/* Роли — справочная подпись, и берутся они с СЕРВЕРА целиком, вместе с названиями
                    (issue #951). Собирать подпись по техническому имени клиент больше не умеет и не
                    должен: имён девять системных плюс те, что заводит администратор, и три места
                    интерфейса успели подписать одно и то же имя по-разному.
                    Ролей может быть несколько (issue #984) — перечисляем все: показанная первая
                    означала бы, что человек носит меньше прав, чем носит. */}
                <span className="block text-xs text-fg3 truncate">
                  {account?.roles.map(r => r.title).join(', ') || ''}
                </span>
              </span>
              {!collapsed && <ChevronsUpDown size={18} className="text-fg3 shrink-0" />}
            </NavLink>

            <RailButton icon={KeyRound} label="Сменить пароль" onClick={() => setPwOpen(true)} collapsed={collapsed} />
            {/* Постоянная дверь к форме сообщения об ошибке (issue #834): она нужна и тогда, когда
                ошибка НЕ показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
            <RailButton icon={Bug} label="Сообщить об ошибке" onClick={() => bugReport.open({ origin: 'rail' })}
              collapsed={collapsed} />
            <RailButton icon={LogOut} label="Выйти" onClick={() => guardedLeave(logout)} collapsed={collapsed} />
          </div>

          <VersionLabel collapsed={collapsed} />
        </div>
      </aside>
      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <ShortcutsHelp open={helpOpen} onOpenChange={setHelpOpen} />

      {/* Content */}
      <main className="flex-1 flex flex-col overflow-hidden">
        <header className="h-12 shrink-0 border-b border-stroke bg-surface flex items-center gap-1 px-4">
          {/* Переключатель пространства — слева: это контекст экрана, а не действие (AUTH-16.1). */}
          <WorkspaceSwitcher />
          {/* Избранные стройки — за пространством (#1218): тоже «что я вижу», а не действие. */}
          <SiteFilterSwitch />
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

// Версия приложения (низ сайдбара). Подробности (сборка/дата) — в подсказке при наведении.
//
// Здесь же — ПАССИВНЫЙ носитель новости о новой версии (issue #813): виден на каждом экране, всем и
// всегда, и никого не дёргает. Уведомлением тревожим только администратора — того, у кого есть путь
// к действию; остальным знать полезно («мы на свежей?»), но требовать от них нечего.
//
// В свёрнутой панели (issue #1355) — один номер, а новость о версии — его цветом: носитель обязан
// остаться виден и там, подробности те же, в подсказке.
function VersionLabel({ collapsed }: { collapsed: boolean }) {
  const { data } = useAppVersion();
  const { user } = useAuth();
  const { data: update } = useUpdateStatus(!!user);
  if (!data) return null;
  const newer = update?.updateAvailable ? update.latest : null;
  const title = [
    `Версия ${data.version}`,
    data.commit && `сборка ${data.commit}`,
    data.buildDate && formatDateTime(data.buildDate),
    // «он уже уведомлён» — не вежливость, а факт: администраторам уходит адресное уведомление
    // (issue #813). Без этого «обновление выполняет администратор» читается как поручение сходить
    // и сказать ему, то есть открывает читателю задачу вместо того, чтобы закрыть вопрос.
    newer && `доступна версия ${newer} — обновление устанавливает администратор, он уже уведомлён`,
  ].filter(Boolean).join(' · ');
  if (collapsed) {
    return (
      <div className={`text-caption truncate text-center pt-2.5 pb-1.5 ${newer ? 'text-brand' : 'text-fg3'}`} title={title}>
        v{data.version}
      </div>
    );
  }
  return (
    <div className="text-caption text-fg3 truncate px-4 pt-2.5 pb-1.5" title={title}>
      v{data.version}{data.commit ? ` · ${data.commit}` : ''}
      {newer && <span className="text-brand"> · доступна {newer}</span>}
    </div>
  );
}
