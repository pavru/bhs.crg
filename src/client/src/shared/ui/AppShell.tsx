import { useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '@/shared/hooks/useAuth';
import { useTheme, type Theme } from '@/shared/ui/ThemeProvider';
import { NotificationsCenter } from '@/features/notifications/NotificationsCenter';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import {
  FolderOpen, BookOpen, FileText, Settings, LogOut, Building2,
  Sun, Moon, Monitor, Layers, Database, Tag, ShieldCheck, Users, KeyRound,
} from 'lucide-react';

const workNav = [
  { to: '/document-sets', label: 'Стройки',        icon: FolderOpen },
  { to: '/catalog',       label: 'Каталог',         icon: Building2  },
  { to: '/common-data',   label: 'Общие данные',    icon: Database   },
  { to: '/datasets',      label: 'Наборы данных',   icon: Layers     },
  { to: '/quality-docs',  label: 'Документы качества', icon: ShieldCheck },
];

const settingsNav = [
  { to: '/document-types',  label: 'Типы документов', icon: BookOpen  },
  { to: '/composite-types', label: 'Составные типы',  icon: Layers    },
  { to: '/field-types',     label: 'Типы полей',      icon: Tag       },
  { to: '/templates',       label: 'Шаблоны',         icon: FileText  },
  { to: '/users',           label: 'Пользователи',    icon: Users     },
  { to: '/settings',        label: 'Настройки',       icon: Settings  },
];

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
  items: { to: string; label: string; icon: typeof FolderOpen }[];
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
              `group flex items-center gap-3 px-3 py-2 rounded-md text-sm font-medium transition-colors select-none ${
                isActive ? 'bg-brand-subtle' : ''
              }`
            }
          >
            {({ isActive }) => (
              <>
                <Icon
                  size={16}
                  className={`shrink-0 ${isActive ? 'text-brand' : 'text-fg3'}`}
                />
                <span className={isActive ? 'text-brand' : 'text-fg1'}>
                  {itemLabel}
                </span>
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
          <div className="text-xs truncate text-fg3">
            {user?.displayName || user?.email}
            <span className="ml-1 text-fg4">· {isAdmin ? 'Администратор' : 'Пользователь'}</span>
          </div>
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
        </div>
      </aside>
      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />

      {/* Content */}
      <main className="flex-1 flex flex-col overflow-hidden">
        <header className="h-12 shrink-0 border-b border-stroke bg-surface flex items-center justify-end px-4">
          <NotificationsCenter />
        </header>
        <div className="flex-1 overflow-auto">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
