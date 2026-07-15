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
import { LogOut, Sun, Moon, Monitor, KeyRound, Check, ChevronsUpDown, MailWarning, X } from 'lucide-react';

const themeOptions: { value: Theme; icon: typeof Sun; label: string }[] = [
  { value: 'light',  icon: Sun,     label: 'Светлая'   },
  { value: 'dark',   icon: Moon,    label: 'Тёмная'    },
  { value: 'system', icon: Monitor, label: 'Системная' },
];

// MD3 segmented button (issue #157): выбранный сегмент — tonal + галочка.
function ThemeToggle() {
  const { theme, setTheme } = useTheme();
  return (
    <div role="group" aria-label="Тема оформления"
      className="flex h-10 rounded-full border border-stroke-strong overflow-hidden">
      {themeOptions.map(({ value, icon: Icon, label }, i) => {
        const active = theme === value;
        return (
          <button key={value} type="button" onClick={() => setTheme(value)}
            title={label} aria-label={label} aria-pressed={active}
            className={`flex-1 flex items-center justify-center gap-1 text-xs transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ` +
              `${i > 0 ? 'border-l border-stroke-strong' : ''} ` +
              (active ? 'bg-tonal text-on-tonal' : 'text-fg3 hover:bg-black/5 dark:hover:bg-white/10')}>
            {active && <Check size={14} className="shrink-0" />}
            <Icon size={16} />
          </button>
        );
      })}
    </div>
  );
}

/** Инициалы для аватара: 2 буквы из имени (или email). */
function initialsOf(name?: string, email?: string): string {
  const src = (name || email || '').trim();
  const parts = src.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return src.slice(0, 2).toUpperCase();
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

        {/* Bottom: theme + user (MD3 drawer footer, issue #157) */}
        <div className="px-3 pt-3 pb-1 border-t border-stroke shrink-0">
          <div className="mb-3"><ThemeToggle /></div>

          <div className="space-y-0.5">
            {/* Блок пользователя — пилюля с аватаром */}
            <NavLink to="/profile"
              className={({ isActive }) => `flex items-center gap-3 h-14 px-4 rounded-[28px] transition-colors ` +
                `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ` +
                (isActive ? 'bg-tonal' : 'hover:bg-black/5 dark:hover:bg-white/10')}>
              <span className="flex items-center justify-center w-10 h-10 rounded-full shrink-0 bg-brand-subtle text-on-brand-subtle text-[15px] font-medium">
                {initialsOf(user?.displayName, user?.email)}
              </span>
              <span className="flex-1 min-w-0">
                <span className="block text-sm font-medium text-fg1 truncate">{user?.displayName || user?.email}</span>
                <span className="block text-xs text-fg3">{isAdmin ? 'Администратор' : 'Пользователь'}</span>
              </span>
              <ChevronsUpDown size={18} className="text-fg3 shrink-0" />
            </NavLink>

            <button type="button" onClick={() => setPwOpen(true)}
              className="w-full flex items-center gap-3 h-14 px-4 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
              <KeyRound size={22} className="text-fg3 shrink-0" />
              <span className="text-sm font-medium text-fg1">Сменить пароль</span>
            </button>
            <button type="button" onClick={logout}
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
    <div className="text-[11px] text-fg3 truncate px-4 pt-2.5 pb-1.5" title={title}>
      v{data.version}{data.commit ? ` · ${data.commit}` : ''}
    </div>
  );
}
