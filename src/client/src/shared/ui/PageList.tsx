import { NavLink } from 'react-router';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import type { PanelGroup } from '@/shared/ui/railSections';
import { MICRO_HEADING, panelLink } from '@/shared/ui/panelLink';
import { PageRow } from '@/shared/ui/PinnedGroup';
import { usePinAction } from '@/shared/ui/usePinAction';

/**
 * Страницы раздела по группам — в подпанели рабочего стола. На телефоне те же группы показывает меню
 * заголовка (`SectionMenu`, #1434). Значков здесь нет (issue #1429):
 * значок раздела стоит в рейле, а у двадцати страниц своих различимых значков не набрать — два из
 * них уже повторялись.
 */
export function PageList({ groups, pinnable }: {
  groups: PanelGroup[];
  /** Страница ли это навигации — её можно закрепить (work-04). Избранная стройка — запись, а не страница. */
  pinnable: (to: string) => boolean;
}) {
  const guardedClick = useGuardedNavClick();
  const pinAction = usePinAction();
  return (
    <div className="space-y-3">
      {groups.map((group, i) => (
        <div key={group.label ?? i}>
          {group.label && <h3 className={MICRO_HEADING}>{group.label}</h3>}
          <div className="space-y-0.5">
            {group.items.map(({ to, label }) => {
              const link = (
                <NavLink key={to} to={to} onClick={guardedClick(to)}
                  className={({ isActive }) => `${panelLink(isActive)}${pinnable(to) ? ' pr-12' : ''}`}>
                  <span className="truncate">{label}</span>
                </NavLink>
              );
              // «Закрепить страницу» — в меню «⋮» строки (решение владельца по Р0).
              return pinnable(to) ? <PageRow key={to} label={label} actions={[pinAction(to)]}>{link}</PageRow> : link;
            })}
          </div>
        </div>
      ))}
    </div>
  );
}
