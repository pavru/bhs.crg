import { formatDateTime } from '@/shared/format/format';
import type { ReactNode } from 'react';
import { NavLink } from 'react-router';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useAccount } from '@/shared/api/account';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { Avatar } from '@/shared/ui/Avatar';
import { workNav, settingsNav, type NavItem } from '@/shared/ui/navConfig';
import { visibleNav } from '@/shared/ui/navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useGuardedLeave, useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { splitNav } from '@/shared/ui/workspaces';
import { LogOut, KeyRound, ChevronsUpDown, Bug, type LucideIcon } from 'lucide-react';

/**
 * Содержимое боковой панели: меню, тема, человек, версия. Одно на два места (issue #1355) — панель
 * у края на рабочем столе и выдвижное меню на узком экране. Две копии разошлись бы молча: пункт,
 * добавленный в одну, пропал бы с телефона, и выглядело бы это как «нет прав».
 *
 * `onPick` — выбран пункт или кнопка: выдвижное меню по нему закрывается, иначе оно оставалось бы
 * лежать поверх открытого раздела. Тема и «Ещё N разделов» его не зовут — они меняют саму панель.
 */
export function RailBody({ collapsed, head, navId, onChangePassword, onPick }: {
  collapsed: boolean;
  /** Строка над меню: название с кнопкой свёртки (панель) или с кнопкой закрытия (выдвижное меню). */
  head: ReactNode;
  navId: string;
  onChangePassword: () => void;
  onPick?: () => void;
}) {
  const { user, logout } = useAuth();
  const { data: account } = useAccount();
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
  const bugReport = useBugReportDialog();
  const guardedProfileClick = useGuardedNavClick();
  const guardedLeave = useGuardedLeave();
  const picked = (act: () => void) => () => { onPick?.(); act(); };

  return (
    <>
      {head}

      <nav id={navId} className={`flex-1 py-3 space-y-4 overflow-y-auto ${collapsed ? 'px-3' : 'px-2'}`}>
        {/* Группа есть, пока в ней есть хоть что-то — пункты пространства ИЛИ строка скрытых (ревью
            PR #1330): иначе «Ещё N разделов» стояла бы без заголовка и без черты перед настройкой. */}
        {hasWork && (
          <div>
            <NavSection label="Документы и данные" items={work} collapsed={collapsed} onPick={onPick} />
            <OutsideNav items={outside} collapsed={collapsed} onPick={onPick} />
          </div>
        )}
        {settings.length > 0 && (
          <>
            {hasWork && <div className="border-t border-stroke mt-2" />}
            <NavSection label="Настройка системы" items={settings} collapsed={collapsed} onPick={onPick} />
          </>
        )}
      </nav>

      {/* Bottom: theme + user (MD3 drawer footer, issue #157) */}
      <div className="px-3 pt-3 pb-1 border-t border-stroke shrink-0">
        <div className={collapsed ? 'mb-3 flex justify-center' : 'mb-3'}><ThemeToggle vertical={collapsed} /></div>

        <div className="space-y-0.5">
          {/* Блок пользователя — пилюля с аватаром; в свёрнутой панели — один аватар. */}
          <NavLink to="/profile" onClick={e => { onPick?.(); guardedProfileClick('/profile')(e); }}
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

          <RailButton icon={KeyRound} label="Сменить пароль" onClick={picked(onChangePassword)} collapsed={collapsed} />
          {/* Постоянная дверь к форме сообщения об ошибке (issue #834): она нужна и тогда, когда
              ошибка НЕ показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
          <RailButton icon={Bug} label="Сообщить об ошибке" onClick={picked(() => bugReport.open({ origin: 'rail' }))}
            collapsed={collapsed} />
          <RailButton icon={LogOut} label="Выйти" onClick={picked(() => guardedLeave(logout))} collapsed={collapsed} />
        </div>

        <VersionLabel collapsed={collapsed} />
      </div>
    </>
  );
}

function NavSection({ label, items, collapsed, onPick }: {
  label: string;
  items: NavItem[];
  collapsed: boolean;
  onPick?: () => void;
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
            onClick={e => { onPick?.(); guardedClick(to)(e); }}
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
