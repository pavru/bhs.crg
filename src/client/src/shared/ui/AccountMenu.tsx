import { useLocation } from 'react-router';
import { ownerItem, type RailSection } from '@/shared/ui/railSections';
import { Feedback, Logout, Person } from './icons';
import { formatDateTime } from '@/shared/format/format';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAccount } from '@/shared/api/account';
import { useAppVersion, type AppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useTheme } from '@/shared/ui/themeContext';
import { Avatar } from '@/shared/ui/Avatar';
import { useGuardedLeave } from '@/shared/ui/NavigationGuard';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { THEME_OPTIONS } from '@/shared/ui/themeOptions';
import { Tooltip } from './Tooltip';
import { Menu, MenuItem, MenuLabel, MenuRadioGroup, MenuRadioItem, MenuSeparator } from './Menu';

/**
 * Меню профиля в шапке (issue #1430) — только то, что касается самого человека: кто он, профиль и
 * пароль, оформление, сообщение о проблеме, выход, версия. Ни системы, ни уведомлений, ни настройки:
 * у них свои места (#1431, рейл «Система»).
 *
 * Пароль меняется только в профиле: своего окна смены пароля у оболочки больше нет — два входа в одно
 * и то же действие с разных мест расходятся.
 *
 * Клавиатура — у Radix: стрелки по пунктам, Esc закрывает и возвращает фокус на аватар. Тема — три
 * варианта с галочкой у выбранного ([UI-76]), а не ряд сегментов: меню — пункты, а не панель. Подсказки
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

  return (
    <Menu align="end" sideOffset={8} className="w-80" trigger={
      // На телефоне — поле вокруг аватара: видимые 44 px, а не 36 (#1434); при касании цель 48 —
      // `touch-target` на 2 px наружу ([UI-68]).
      <button type="button" data-testid="account-menu"
        aria-label={`${name} — профиль и оформление${newer ? `; доступна версия ${newer}` : ''}`}
        className={'relative shrink-0 rounded-full max-md:p-1 touch-target [--_touch-inset:-2px] focus-ring data-[state=open]:ring-2 data-[state=open]:ring-primary ' +
          (here || inAdmin ? 'ring-2 ring-primary' : '')}>
        <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-9 h-9 text-body-medium" />
        {newer && <span data-testid="update-dot" aria-hidden="true"
          className="absolute -top-0.5 -right-0.5 max-md:top-0.5 max-md:right-0.5 w-2.5 h-2.5 rounded-full bg-primary ring-2 ring-surface" />}
      </button>
    }>
      <div className="flex items-center gap-3 px-3 pt-2 pb-3">
        <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} className="w-10 h-10 text-body-large" />
        <div className="min-w-0">
          <div className="text-body-large text-on-surface truncate">{name}</div>
          {roles && <div className="text-body-small text-on-surface-variant truncate">{roles}</div>}
          {user?.displayName && <div className="text-body-small text-on-surface-variant truncate">{user.email}</div>}
        </div>
      </div>

      <MenuItem icon={Person} to="/profile" current={here}>Профиль и пароль</MenuItem>

      {/* Подписи словами (#1430): значок монитора сам по себе «как в системе» не читается.
          Пояснение к «Авто» — строкой, а не подсказкой: подсказка, открытая фокусом с клавиатуры,
          забрала бы себе первый Esc, и меню не закрылось бы.
          Выбор меню не закрывает — новая тема видна сразу, и её можно сменить ещё раз. */}
      <MenuSeparator />
      <MenuLabel className="flex items-baseline gap-2">
        <span className="flex-1">Оформление</span>
        <span className="text-body-small">{autoHint}</span>
      </MenuLabel>
      <MenuRadioGroup value={theme} onValueChange={v => setTheme(v as typeof theme)}>
        {THEME_OPTIONS.map(({ value, icon, label }) => (
          <MenuRadioItem key={value} value={value} icon={icon} onSelect={e => e.preventDefault()}>
            {label}
          </MenuRadioItem>
        ))}
      </MenuRadioGroup>
      <MenuSeparator />

      {/* Постоянная дверь к форме сообщения (issue #834): она нужна и тогда, когда ошибка НЕ
          показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
      <MenuItem icon={Feedback} onSelect={() => bugReport.open({ origin: 'account' })}>Сообщить о проблеме</MenuItem>

      {admin && adminIcon && (
        <>
          <MenuSeparator />
          <MenuItem icon={adminIcon} to={admin.home} current={inAdmin} data-testid="account-admin">
            {admin.def.title}
          </MenuItem>
        </>
      )}

      <MenuSeparator />
      <MenuItem icon={Logout} onSelect={() => guardedLeave(logout)}>Выйти</MenuItem>

      {version && <VersionLine version={version} newer={newer} />}
    </Menu>
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
      <div data-testid="app-version" className="px-3 pt-2 pb-1 text-label-small text-on-surface-variant truncate">
        Версия {data.version}{data.commit ? ` · ${data.commit}` : ''}
        {newer && <span className="text-primary"> · доступна {newer}</span>}
      </div>
    </Tooltip>
  );
}
