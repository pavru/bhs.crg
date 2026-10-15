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
import { LogOut, KeyRound, ChevronsUpDown, MailWarning, X, Bug } from 'lucide-react';


function NavSection({
  label,
  items,
}: {
  label: string;
  items: NavItem[];
}) {
  const guardedClick = useGuardedNavClick();
  return (
    <div>
      <p className="px-3 mb-1 text-caption font-semibold uppercase tracking-widest select-none text-fg4">
        {label}
      </p>
      <div className="space-y-0.5">
        {items.map(({ to, label: itemLabel, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
            onClick={guardedClick(to)}
            className={({ isActive }) =>
              `group flex items-center gap-3 px-3 py-2.5 rounded-full text-sm font-medium select-none transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
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
                <span>{itemLabel}</span>
              </>
            )}
          </NavLink>
        ))}
      </div>
    </div>
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
      <aside className="w-56 flex flex-col border-r border-stroke bg-surface">
        {/* Brand */}
        {/* Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. */}
        <div className="flex items-center gap-2 px-4 h-12 border-b border-stroke shrink-0">
          <BrandLogo className="w-6 h-6 text-brand" iconSize={18} />
          <span className="text-base font-semibold tracking-tight text-brand truncate">
            {productName}
          </span>
        </div>

        {/* Nav sections */}
        <nav className="flex-1 px-2 py-3 space-y-4 overflow-y-auto">
          {/* Группа есть, пока в ней есть хоть что-то — пункты пространства ИЛИ строка скрытых (ревью
              PR #1330): иначе «Ещё N разделов» стояла бы без заголовка и без черты перед настройкой. */}
          {hasWork && (
            <div>
              <NavSection label="Документы и данные" items={work} />
              <OutsideNav items={outside} />
            </div>
          )}
          {settings.length > 0 && (
            <>
              {hasWork && <div className="border-t border-stroke mt-2" />}
              <NavSection label="Настройка системы" items={settings} />
            </>
          )}
        </nav>

        {/* Bottom: theme + user (MD3 drawer footer, issue #157) */}
        <div className="px-3 pt-3 pb-1 border-t border-stroke shrink-0">
          <div className="mb-3"><ThemeToggle /></div>

          <div className="space-y-0.5">
            {/* Блок пользователя — пилюля с аватаром */}
            <NavLink to="/profile" onClick={guardedProfileClick('/profile')}
              className={({ isActive }) => `flex items-center gap-3 h-14 px-4 rounded-[28px] transition-colors ` +
                `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ` +
                (isActive ? 'bg-tonal' : 'hover:bg-black/5 dark:hover:bg-white/10')}>
              <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} />

              <span className="flex-1 min-w-0">
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
              <ChevronsUpDown size={18} className="text-fg3 shrink-0" />
            </NavLink>

            <button type="button" onClick={() => setPwOpen(true)}
              className="w-full flex items-center gap-3 h-14 px-4 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
              <KeyRound size={22} className="text-fg3 shrink-0" />
              <span className="text-sm font-medium text-fg1">Сменить пароль</span>
            </button>
            {/* Постоянная дверь к форме сообщения об ошибке (issue #834): она нужна и тогда, когда
                ошибка НЕ показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
            <button type="button" onClick={() => bugReport.open({ origin: 'rail' })}
              className="w-full flex items-center gap-3 h-14 px-4 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
              <Bug size={22} className="text-fg3 shrink-0" />
              <span className="text-sm font-medium text-fg1">Сообщить об ошибке</span>
            </button>
            <button type="button" onClick={() => guardedLeave(logout)}
              className="w-full flex items-center gap-3 h-14 px-4 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
              <LogOut size={22} className="text-fg3 shrink-0" />
              <span className="text-sm font-medium text-fg1">Выйти</span>
            </button>
          </div>

          <VersionLabel />
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
function VersionLabel() {
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
  return (
    <div className="text-caption text-fg3 truncate px-4 pt-2.5 pb-1.5" title={title}>
      v{data.version}{data.commit ? ` · ${data.commit}` : ''}
      {newer && <span className="text-brand"> · доступна {newer}</span>}
    </div>
  );
}
