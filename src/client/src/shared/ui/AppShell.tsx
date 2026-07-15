import { useState, useEffect } from 'react';
import { NavLink, Link, Outlet } from 'react-router-dom';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { useAccount, useResendConfirmation } from '@/shared/api/account';
import { useTheme, type Theme } from '@/shared/ui/ThemeProvider';
import { NotificationsCenter } from '@/features/notifications/NotificationsCenter';
import { ActiveJobsIndicator } from '@/features/jobs/ActiveJobsIndicator';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { CommandPalette } from '@/shared/ui/CommandPalette';
import { ShortcutsHelp } from '@/shared/ui/ShortcutsHelp';
import { workNav, settingsNav, type NavItem } from '@/shared/ui/navConfig';
import { LogOut, Sun, Moon, Monitor, KeyRound, UserRound, MailWarning, X } from 'lucide-react';

const themeOptions: { value: Theme; icon: typeof Sun; label: string }[] = [
  { value: 'light',  icon: Sun,     label: 'Светлая'   },
  { value: 'dark',   icon: Moon,    label: 'Тёмная'    },
  { value: 'system', icon: Monitor, label: 'Системная' },
];

function ThemeToggle() {
  const { theme, setTheme } = useTheme();
  return (
    <div
      className="flex items-center gap-0.5 rounded-md p-0.5 bg-muted"
      title="Тема оформления"
    >
      {themeOptions.map(({ value, icon: Icon, label }) => (
        <button
          key={value}
          onClick={() => setTheme(value)}
          title={label}
          className={`flex items-center justify-center w-7 h-7 rounded transition-colors ${
            theme === value ? 'text-fg1 shadow-sm bg-surface' : 'text-fg3 hover:text-fg2'
          }`}
        >
          <Icon size={14} />
        </button>
      ))}
    </div>
  );
}

function NavSection({
  label,
  items,
}: {
  label: string;
  items: NavItem[];
}) {
  return (
    <div>
      <p className="px-3 mb-1 text-[10px] font-semibold uppercase tracking-widest select-none text-fg4">
        {label}
      </p>
      <div className="space-y-0.5">
        {items.map(({ to, label: itemLabel, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
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

export function AppShell() {
  const { user, logout } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const [pwOpen, setPwOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);

  // Глобальные шорткаты (issue #107): Ctrl/⌘+K — палитра, «?» — справка (не в полях ввода).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen(o => !o);
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
        <div className="flex items-center px-4 h-12 border-b border-stroke shrink-0">
          <span className="text-base font-semibold tracking-tight text-brand">
            BHS.CRG
          </span>
        </div>

        {/* Nav sections */}
        <nav className="flex-1 px-2 py-3 space-y-4 overflow-y-auto">
          <NavSection label="Документы и данные" items={workNav} />
          {isAdmin && (
            <>
              <div className="border-t border-stroke mt-2" />
              <NavSection label="Настройка системы" items={settingsNav} />
            </>
          )}
        </nav>

        {/* Bottom: theme + user */}
        <div className="px-3 py-3 border-t border-stroke space-y-3 shrink-0">
          <ThemeToggle />
          <NavLink to="/profile"
            className={({ isActive }) => `flex items-center gap-2 text-xs truncate transition-colors ` +
              (isActive ? 'text-brand' : 'text-fg3 hover:text-brand')}>
            <UserRound size={14} className="shrink-0" />
            <span className="truncate">
              {user?.displayName || user?.email}
              <span className="ml-1 text-fg4">· {isAdmin ? 'Администратор' : 'Пользователь'}</span>
            </span>
          </NavLink>
          <button
            onClick={() => setPwOpen(true)}
            className="flex items-center gap-2 text-sm transition-colors w-full text-fg2 hover:text-brand"
          >
            <KeyRound size={14} /> Сменить пароль
          </button>
          <button
            onClick={logout}
            className="flex items-center gap-2 text-sm transition-colors w-full text-fg2 hover:text-danger"
          >
            <LogOut size={14} /> Выйти
          </button>
          <VersionLabel />
        </div>
      </aside>
      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <ShortcutsHelp open={helpOpen} onOpenChange={setHelpOpen} />

      {/* Content */}
      <main className="flex-1 flex flex-col overflow-hidden">
        <header className="h-12 shrink-0 border-b border-stroke bg-surface flex items-center justify-end gap-1 px-4">
          <ActiveJobsIndicator />
          <NotificationsCenter />
        </header>
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
        <Link to="/profile" className="font-medium underline hover:no-underline">Профиль</Link>
      </span>
      <button type="button" onClick={() => setDismissed(true)} aria-label="Скрыть"
        className="shrink-0 p-1 rounded hover:bg-warning/10">
        <X size={14} />
      </button>
    </div>
  );
}

// Версия приложения (низ сайдбара). Подробности (сборка/дата) — в подсказке при наведении.
function VersionLabel() {
  const { data } = useAppVersion();
  if (!data) return null;
  const title = [
    `Версия ${data.version}`,
    data.commit && `сборка ${data.commit}`,
    data.buildDate && new Date(data.buildDate).toLocaleString('ru-RU'),
  ].filter(Boolean).join(' · ');
  return (
    <div className="text-[10px] text-fg4 truncate pt-1" title={title}>
      v{data.version}{data.commit ? ` · ${data.commit}` : ''}
    </div>
  );
}
