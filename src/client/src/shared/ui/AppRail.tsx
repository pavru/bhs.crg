import { formatDateTime } from '@/shared/format/format';
import type { ReactNode } from 'react';
import { NavLink } from 'react-router';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { useUpdateStatus } from '@/shared/api/updates';
import { useAccount } from '@/shared/api/account';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { Avatar } from '@/shared/ui/Avatar';
import { useGuardedLeave, useGuardedPick } from '@/shared/ui/NavigationGuard';
import { useBugReportDialog } from '@/shared/ui/bugReportBus';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { useNavSections } from '@/shared/ui/navSections';
import type { PanelGroup } from '@/shared/ui/railSections';
import { panelLink } from '@/shared/ui/panelLink';
import { LogOut, KeyRound, Bug, type LucideIcon } from 'lucide-react';
import { Tooltip } from './Tooltip';

/**
 * Выдвижное меню узкого экрана (issue #1355): все разделы рейла разом, каждый со своими страницами.
 * На рабочем столе страницы раздела живут в подпанели (issue #1429), а меню телефона — одно окно, и
 * прятать в нём раздел за разделом значило бы заставить листать. Своя нижняя навигация телефона —
 * #1434.
 *
 * `onPick` — выбран пункт или кнопка: меню по нему закрывается, иначе оно оставалось бы лежать
 * поверх открытого раздела. Тема и «Ещё N разделов» его не зовут — они меняют само меню.
 */
export function DrawerBody({ head, onChangePassword, onPick }: {
  /** Строка над меню: название с кнопкой закрытия. */
  head: ReactNode;
  onChangePassword: () => void;
  onPick: () => void;
}) {
  const { rail, outside } = useNavSections();
  return (
    <>
      {head}
      <nav id="app-drawer-nav" aria-label="Меню" className="flex-1 px-2 py-3 space-y-4 overflow-y-auto">
        {rail.map(section => (
          <div key={section.def.id}>
            {/* Раздел — ступенью выше группы: иначе «Учёт затрат» и «Отчёты и закрытие месяца»
                читались бы соседями. */}
            <h2 className="px-3 mb-1 text-sm font-semibold text-fg1">{section.def.title}</h2>
            <PageList groups={section.groups} onPick={onPick} />
          </div>
        ))}
        <OutsideNav items={outside} onPick={onPick} />
      </nav>
      <RailFooter onChangePassword={onChangePassword} onPick={onPick} />
    </>
  );
}

/** Микрозаголовок навигации и групп — по docs/UI.md, «Заголовки». */
const MICRO_HEADING = 'px-3 mb-1 text-caption font-semibold uppercase tracking-widest select-none text-fg4';

/**
 * Страницы раздела по группам — одно на подпанель и выдвижное меню. Значков здесь нет (issue #1429):
 * значок раздела стоит в рейле, а у двадцати страниц своих различимых значков не набрать — два из
 * них уже повторялись.
 */
export function PageList({ groups, onPick }: { groups: PanelGroup[]; onPick?: () => void }) {
  const guardedClick = useGuardedPick(onPick);
  return (
    <div className="space-y-3">
      {groups.map((group, i) => (
        <div key={group.label ?? i}>
          {group.label && <h3 className={MICRO_HEADING}>{group.label}</h3>}
          <div className="space-y-0.5">
            {group.items.map(({ to, label }) => (
              <NavLink key={to} to={to} onClick={guardedClick(to)}
                className={({ isActive }) => panelLink(isActive)}>
                <span className="truncate">{label}</span>
              </NavLink>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Низ панели: тема, человек, пароль, сообщение об ошибке, выход, версия. `compact` — у рейла рабочего
 * стола (88 px): тема столбиком, остальное значками с подсказками, подписи остаются диктору.
 *
 * Временное место: всё это уходит в меню профиля в шапке (#1430).
 */
export function RailFooter({ compact = false, onChangePassword, onPick }: {
  compact?: boolean;
  onChangePassword: () => void;
  onPick?: () => void;
}) {
  const { user, logout } = useAuth();
  const { data: account } = useAccount();
  const bugReport = useBugReportDialog();
  const guardedProfileClick = useGuardedPick(onPick);
  const guardedLeave = useGuardedLeave();
  const picked = (act: () => void) => () => { onPick?.(); act(); };

  return (
    <div data-testid="rail-footer" className={`pt-3 pb-1 shrink-0 ${compact ? 'w-full' : 'px-3 border-t border-stroke'}`}>
      <div className={compact ? 'mb-3 flex justify-center' : 'mb-3'}><ThemeToggle vertical={compact} /></div>

      <div className="space-y-0.5">
        {/* Блок пользователя — пилюля с аватаром; в рейле — один аватар. */}
        <Tooltip content={compact ? (user?.displayName || user?.email) : undefined} side="right">
          <NavLink to="/profile" onClick={guardedProfileClick('/profile')}
            className={({ isActive }) => `flex items-center gap-3 rounded-[28px] transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ` +
              (compact ? 'justify-center w-12 h-12 mx-auto ' : 'h-14 px-4 ') +
              (isActive ? 'bg-tonal' : 'hover:bg-black/5 dark:hover:bg-white/10')}>
            <Avatar src={account?.avatar} name={user?.displayName} email={user?.email} />

            <span className={compact ? 'sr-only' : 'flex-1 min-w-0'}>
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
          </NavLink>
        </Tooltip>

        <RailButton icon={KeyRound} label="Сменить пароль" onClick={picked(onChangePassword)} collapsed={compact} />
        {/* Постоянная дверь к форме сообщения об ошибке (issue #834): она нужна и тогда, когда
            ошибка НЕ показала своего экрана — «работает не так, как ожидал» тоже сообщение. */}
        <RailButton icon={Bug} label="Сообщить об ошибке" onClick={picked(() => bugReport.open({ origin: 'rail' }))}
          collapsed={compact} />
        <RailButton icon={LogOut} label="Выйти" onClick={picked(() => guardedLeave(logout))} collapsed={compact} />
      </div>

      <VersionLabel collapsed={compact} />
    </div>
  );
}

/** Кнопка низа панели: в свёрнутой — иконка с подсказкой, подпись остаётся для диктора. */
function RailButton({ icon: Icon, label, onClick, collapsed }: {
  icon: LucideIcon; label: string; onClick: () => void; collapsed: boolean;
}) {
  return (
    <Tooltip content={collapsed ? label : undefined}>
      <button type="button" onClick={onClick}
        className={`flex items-center gap-3 rounded-[28px] text-left transition-colors hover:bg-black/5 dark:hover:bg-white/10 ` +
          `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
          collapsed ? 'justify-center w-10 h-10 mx-auto' : 'w-full h-14 px-4'}`}>
        <Icon size={collapsed ? 20 : 22} className="text-fg3 shrink-0" />
        <span className={collapsed ? 'sr-only' : 'text-sm font-medium text-fg1'}>{label}</span>
      </button>
    </Tooltip>
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
      <Tooltip content={title}>
        <div className={`text-caption truncate text-center pt-2.5 pb-1.5 ${newer ? 'text-brand' : 'text-fg3'}`}>
          v{data.version}
        </div>
      </Tooltip>
    );
  }
  return (
    <Tooltip content={title}>
      <div className="text-caption text-fg3 truncate px-4 pt-2.5 pb-1.5">
        v{data.version}{data.commit ? ` · ${data.commit}` : ''}
        {newer && <span className="text-brand"> · доступна {newer}</span>}
      </div>
    </Tooltip>
  );
}
