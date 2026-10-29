import { useState } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Link, useLocation } from 'react-router';
import { ownerItem, type RailSection } from '@/shared/ui/railSections';
import { Icon } from './Icon';
import { Feedback, Logout, Person } from './icons';
import { formatDateTime } from '@/shared/format/format';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAccount } from '@/shared/api/account';
import { useAppVersion, type AppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useTheme } from '@/shared/ui/themeContext';
import { Avatar } from '@/shared/ui/Avatar';
import { useGuardedLeave, useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { THEME_OPTIONS } from '@/shared/ui/themeOptions';
import { Tooltip } from './Tooltip';
import { useHistoryLayer } from './historyLayers';

const item = 'flex items-center gap-3.5 h-11 px-3 rounded-xl text-sm text-on-surface cursor-pointer select-none outline-none ' +
  'data-[highlighted]:bg-black/5 dark:data-[highlighted]:bg-white/10';

/**
 * Меню профиля в шапке (issue #1430) — только то, что касается самого человека: кто он, профиль и
 * пароль, оформление, сообщение о проблеме, выход, версия. Ни системы, ни уведомлений, ни настройки:
 * у них свои места (#1431, рейл «Система»).
 *
 * Пароль меняется только в профиле: своего окна смены пароля у оболочки больше нет — два входа в одно
 * и то же действие с разных мест расходятся.
 *
 * Клавиатура — у Radix: стрелки по пунктам, Esc закрывает и возвращает фокус на аватар. Подсказки
 * у аватара нет: имя уже в его `aria-label`, а подсказка, открытая возвращённым фокусом, забирала бы
 * себе следующий Esc.
 *
 * `admin` — раздел «Система» на телефоне (issue #1434, решение владельца от 17.10.2026): рейла там
 * нет, а нижняя навигация — для работы. Пункт «Администрирование» отделён от личных чертой: он не
 * про человека. Есть только у того, кому открыта хоть одна страница раздела, — иначе раздела нет и
 * в `rail`. На рабочем столе его не передают: там «Система» стоит в рейле.
 */
export function AccountMenu({ admin }: { admin?: RailSection }) {
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
  // Версия читается вместе с оболочкой, а не с открытием меню: новость о новой версии — ПАССИВНЫЙ
  // носитель, видный на каждом экране (issue #813), — точка на аватаре. И меню не подрастает на
  // глазах, дождавшись ответа.
  const { data: version } = useAppVersion();
  const { data: update } = useUpdateStatus(!!user);
  const newer = update?.updateAvailable ? update.latest : null;
  // Профиль — страница без раздела рейла: «вы здесь» показывает аватар. На телефоне — и страницы
  // администрирования: их пункт тоже живёт в этом меню.
  const { pathname } = useLocation();
  const here = pathname.startsWith('/profile');
  const inAdmin = !!admin && !!ownerItem(pathname, admin.groups.flatMap(g => g.items));
  const adminIcon = admin?.def.icon;
  // «Назад» на телефоне закрывает меню, а не страницу (issue #1418).
  const [open, setOpen] = useState(false);
  useHistoryLayer(open, () => { setOpen(false); return true; });

  return (
    <DropdownMenu.Root open={open} onOpenChange={setOpen}>
      <DropdownMenu.Trigger asChild>
        {/* На телефоне — поле вокруг аватара: цель касания 44 px, а не 36 (#1434). */}
        <button type="button" data-testid="account-menu"
          aria-label={`${name} — профиль и оформление${newer ? `; доступна версия ${newer}` : ''}`}
          className={'relative shrink-0 rounded-full max-md:p-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ' +
            'focus-visible:ring-offset-2 focus-visible:ring-offset-surface data-[state=open]:ring-2 data-[state=open]:ring-primary ' +
            (here || inAdmin ? 'ring-2 ring-primary' : '')}>
          <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-9 h-9 text-sm" />
          {newer && <span data-testid="update-dot" aria-hidden="true"
            className="absolute -top-0.5 -right-0.5 max-md:top-0.5 max-md:right-0.5 w-2.5 h-2.5 rounded-full bg-primary ring-2 ring-surface" />}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={8}
          className="z-50 w-80 max-w-[calc(100vw-1rem)] rounded-2xl border border-outline-variant bg-surface overflow-hidden focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          <div className="flex items-center gap-3.5 px-5 pt-5 pb-3">
            <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-12 h-12 text-base" />
            <div className="min-w-0">
              <div className="text-base font-medium text-on-surface truncate">{name}</div>
              {roles && <div className="text-xs text-on-surface-variant truncate">{roles}</div>}
              {user?.displayName && <div className="text-xs text-on-surface-variant truncate">{user.email}</div>}
            </div>
          </div>

          <div className="px-2 pb-2">
            <DropdownMenu.Item asChild className={item + (here ? ' bg-secondary-container text-on-secondary-container' : '')}>
              <Link to="/profile" onClick={guardedClick('/profile')} aria-current={here ? 'page' : undefined}>
                <Icon icon={Person} size={20} className="text-on-surface-variant shrink-0" />Профиль и пароль
              </Link>
            </DropdownMenu.Item>

            {/* Подписи словами (#1430): значок монитора сам по себе «как в системе» не читается.
                Пояснение к «Авто» — строкой, а не подсказкой: подсказка, открытая фокусом с клавиатуры,
                забрала бы себе первый Esc, и меню не закрылось бы.
                Выбор меню не закрывает — новая тема видна сразу, и её можно сменить ещё раз. */}
            <DropdownMenu.Group className="px-3 pt-2 pb-2.5">
              <div className="flex items-baseline gap-2 pb-1.5 text-xs">
                <DropdownMenu.Label className="flex-1 font-medium text-on-surface-variant">Оформление</DropdownMenu.Label>
                <span className="text-on-surface-variant">{autoHint}</span>
              </div>
              <DropdownMenu.RadioGroup value={theme} onValueChange={v => setTheme(v as typeof theme)}
                className="flex rounded-full border border-outline overflow-hidden">
                {THEME_OPTIONS.map(({ value, icon, label }, i) => (
                  <DropdownMenu.RadioItem key={value} value={value} onSelect={e => e.preventDefault()}
                    className={'flex-1 flex items-center justify-center gap-1.5 h-9 text-xs cursor-pointer select-none outline-none ' +
                      'data-[highlighted]:ring-2 data-[highlighted]:ring-inset data-[highlighted]:ring-primary ' +
                      (i > 0 ? 'border-l border-outline ' : '') +
                      (theme === value ? 'bg-secondary-container text-on-secondary-container font-medium' : 'text-on-surface-variant hover:bg-black/5 dark:hover:bg-white/10')}>
                    <Icon icon={icon} size={18} className="shrink-0" />{label}
                  </DropdownMenu.RadioItem>
                ))}
              </DropdownMenu.RadioGroup>
            </DropdownMenu.Group>

            {/* Постоянная дверь к форме сообщения (issue #834): она нужна и тогда, когда ошибка НЕ
                показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
            <DropdownMenu.Item className={item} onSelect={() => bugReport.open({ origin: 'account' })}>
              <Icon icon={Feedback} size={20} className="text-on-surface-variant shrink-0" />Сообщить о проблеме
            </DropdownMenu.Item>
          </div>

          {admin && adminIcon && (
            <>
              <DropdownMenu.Separator className="h-px bg-outline-variant" />
              <div className="p-2">
                <DropdownMenu.Item asChild className={item + (inAdmin ? ' bg-secondary-container text-on-secondary-container' : '')}>
                  <Link to={admin.home} onClick={guardedClick(admin.home)} data-testid="account-admin">
                    <Icon icon={adminIcon} size={20} className="text-on-surface-variant shrink-0" />{admin.def.title}
                  </Link>
                </DropdownMenu.Item>
              </div>
            </>
          )}

          <DropdownMenu.Separator className="h-px bg-outline-variant" />
          <div className="p-2">
            <DropdownMenu.Item className={item} onSelect={() => guardedLeave(logout)}>
              <Icon icon={Logout} size={20} className="text-on-surface-variant shrink-0" />Выйти
            </DropdownMenu.Item>
          </div>

          {version && <VersionLine version={version} newer={newer} />}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/**
 * Версия приложения — низ меню профиля. Подробности (сборка, дата) — в подсказке.
 *
 * Здесь же — подробность новости о новой версии (issue #813; сама новость — точка на аватаре): никого
 * не дёргает. Уведомлением
 * тревожим только администратора — того, у кого есть путь к действию; остальным знать полезно
 * («мы на свежей?»), но требовать от них нечего.
 */
function VersionLine({ version: data, newer }: {
  version: AppVersion;
  newer: string | null;
}) {
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
      <div data-testid="app-version" className="px-5 py-2.5 bg-surface-container-low text-caption text-on-surface-variant truncate">
        Версия {data.version}{data.commit ? ` · ${data.commit}` : ''}
        {newer && <span className="text-primary"> · доступна {newer}</span>}
      </div>
    </Tooltip>
  );
}
