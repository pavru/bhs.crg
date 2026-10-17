import type { ReactNode } from 'react';
import { NavLink } from 'react-router';
import { useGuardedPick } from '@/shared/ui/NavigationGuard';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { useNavSections } from '@/shared/ui/navSections';
import type { PanelGroup } from '@/shared/ui/railSections';
import { panelLink } from '@/shared/ui/panelLink';

/**
 * Выдвижное меню узкого экрана (issue #1355): все разделы рейла разом, каждый со своими страницами.
 * На рабочем столе страницы раздела живут в подпанели (issue #1429), а меню телефона — одно окно, и
 * прятать в нём раздел за разделом значило бы заставить листать. Своя нижняя навигация телефона —
 * #1434.
 *
 * `onPick` — выбран пункт: меню по нему закрывается, иначе оно оставалось бы лежать поверх
 * открытого раздела. «Ещё N разделов» его не зовёт — оно меняет само меню.
 *
 * Профиля, темы и выхода здесь нет: аватар с меню профиля стоит в шапке и на узком экране (#1430).
 */
export function DrawerBody({ head, onPick }: {
  /** Строка над меню: название с кнопкой закрытия. */
  head: ReactNode;
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
