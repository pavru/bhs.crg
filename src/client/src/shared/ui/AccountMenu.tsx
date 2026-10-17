import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Link } from 'react-router';
import { LogOut, MessageCircleWarning, UserRound } from 'lucide-react';
import { formatDateTime } from '@/shared/format/format';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAccount } from '@/shared/api/account';
import { useAppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useTheme } from '@/shared/ui/themeContext';
import { Avatar } from '@/shared/ui/Avatar';
import { useGuardedLeave, useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { THEME_OPTIONS } from '@/shared/ui/themeOptions';
import { Tooltip } from './Tooltip';

const item = 'flex items-center gap-3.5 h-11 px-3 rounded-xl text-sm text-fg1 cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-black/5 dark:data-[highlighted]:bg-white/10';

/**
 * Меню профиля в шапке (issue #1430) — только то, что касается самого человека: кто он, профиль и
 * пароль, оформление, сообщение о проблеме, выход, версия. Ни системы, ни уведомлений, ни настройки:
 * у них свои места (#1431, рейл «Система»).
 *
 * Пароль меняется только в профиле: своего окна смены пароля у оболочки больше нет — два входа в одно
 * и то же действие с разных мест расходятся.
 *
 * Клавиатура — у Radix: стрелки по пунктам, Esc закрывает и возвращает фокус на аватар.
 */
export function AccountMenu() {
  const { user, logout } = useAuth();
  const { data: account } = useAccount();
  const { theme, setTheme } = useTheme();
  const bugReport = useBugReportDialog();
  const guardedClick = useGuardedNavClick();
  const guardedLeave = useGuardedLeave();
  const name = user?.displayName || user?.email || '';
  // Роли — подпись с сервера целиком, вместе с названиями (issue #951); их может быть несколько
  // (issue #984) — перечисляем все: показанная первая означала бы меньше прав, чем человек носит.
  const roles = account?.roles.map(r => r.title).join(', ');
  const auto = THEME_OPTIONS.find(o => o.hint);
  const autoHint = auto && `${auto.label.toLowerCase()} — ${auto.hint}`;

  return (
    <DropdownMenu.Root>
      <Tooltip content={name}>
        <DropdownMenu.Trigger asChild>
          <button type="button" data-testid="account-menu" aria-label={`${name} — профиль и оформление`}
            className="shrink-0 rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-surface data-[state=open]:ring-2 data-[state=open]:ring-brand">
            <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-9 h-9 text-sm" />
          </button>
        </DropdownMenu.Trigger>
      </Tooltip>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={8}
          className="z-50 w-80 max-w-[calc(100vw-1rem)] rounded-2xl border border-stroke bg-surface overflow-hidden focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <div className="flex items-center gap-3.5 px-5 pt-5 pb-3">
            <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-12 h-12 text-base" />
            <div className="min-w-0">
              <div className="text-base font-medium text-fg1 truncate">{name}</div>
              {roles && <div className="text-xs text-fg3 truncate">{roles}</div>}
              {user?.displayName && <div className="text-xs text-fg3 truncate">{user.email}</div>}
            </div>
          </div>

          <div className="px-2 pb-2">
            <DropdownMenu.Item asChild className={item}>
              <Link to="/profile" onClick={guardedClick('/profile')}>
                <UserRound size={20} className="text-fg3 shrink-0" />Профиль и пароль
              </Link>
            </DropdownMenu.Item>

            {/* Подписи словами (#1430): значок монитора сам по себе «как в системе» не читается.
                Пояснение к «Авто» — строкой, а не подсказкой: подсказка, открытая фокусом с клавиатуры,
                забрала бы себе первый Esc, и меню не закрылось бы.
                Выбор меню не закрывает — новая тема видна сразу, и её можно сменить ещё раз. */}
            <DropdownMenu.Group className="px-3 pt-2 pb-2.5">
              <div className="flex items-baseline gap-2 pb-1.5 text-xs">
                <DropdownMenu.Label className="flex-1 font-medium text-fg3">Оформление</DropdownMenu.Label>
                <span className="text-fg4">{autoHint}</span>
              </div>
              <DropdownMenu.RadioGroup value={theme} onValueChange={v => setTheme(v as typeof theme)}
                className="flex rounded-full border border-stroke-strong overflow-hidden">
                {THEME_OPTIONS.map(({ value, icon: Icon, label }, i) => (
                  <DropdownMenu.RadioItem key={value} value={value} onSelect={e => e.preventDefault()}
                    className={'flex-1 flex items-center justify-center gap-1.5 h-9 text-xs cursor-pointer select-none outline-none ' +
                      'data-[highlighted]:ring-2 data-[highlighted]:ring-inset data-[highlighted]:ring-brand ' +
                      (i > 0 ? 'border-l border-stroke-strong ' : '') +
                      (theme === value ? 'bg-tonal text-on-tonal font-medium' : 'text-fg2 hover:bg-black/5 dark:hover:bg-white/10')}>
                    <Icon size={16} className="shrink-0" />{label}
                  </DropdownMenu.RadioItem>
                ))}
              </DropdownMenu.RadioGroup>
            </DropdownMenu.Group>

            {/* Постоянная дверь к форме сообщения (issue #834): она нужна и тогда, когда ошибка НЕ
                показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
            <DropdownMenu.Item className={item} onSelect={() => bugReport.open({ origin: 'account' })}>
              <MessageCircleWarning size={20} className="text-fg3 shrink-0" />Сообщить о проблеме
            </DropdownMenu.Item>
          </div>

          <DropdownMenu.Separator className="h-px bg-stroke" />
          <div className="p-2">
            <DropdownMenu.Item className={item} onSelect={() => guardedLeave(logout)}>
              <LogOut size={20} className="text-fg3 shrink-0" />Выйти
            </DropdownMenu.Item>
          </div>

          <VersionLine />
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/**
 * Версия приложения — низ меню профиля. Подробности (сборка, дата) — в подсказке.
 *
 * Здесь же — ПАССИВНЫЙ носитель новости о новой версии (issue #813): никого не дёргает. Уведомлением
 * тревожим только администратора — того, у кого есть путь к действию; остальным знать полезно
 * («мы на свежей?»), но требовать от них нечего.
 */
function VersionLine() {
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
    <Tooltip content={title}>
      <div data-testid="app-version" className="px-5 py-2.5 bg-base text-caption text-fg3 truncate">
        Версия {data.version}{data.commit ? ` · ${data.commit}` : ''}
        {newer && <span className="text-brand"> · доступна {newer}</span>}
      </div>
    </Tooltip>
  );
}
