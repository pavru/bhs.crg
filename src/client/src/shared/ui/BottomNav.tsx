import { Link } from 'react-router';
import { useGuardedNavClick } from './NavigationGuard';
import type { RailId } from './navConfig';
import type { RailSection } from './railSections';
import { SectionPill } from './SectionRail';

/**
 * Нижняя навигация телефона (issue #1434), по образцу MD3 navigation bar: рабочие разделы рейла —
 * значок и подпись под ним. Палец достаёт до низа экрана, а не до гамбургера в углу; разделов
 * немного, и прятать их за меню незачем.
 *
 * Разделы отбирает `phoneChrome`: «Системы» здесь нет — администрирование на телефоне — пункт меню
 * профиля (решение владельца от 17.10.2026), а нижняя строка — для работы. Разделов нет — нет и
 * строки: пустая полоса у края звала бы нажать в никуда.
 *
 * Высота — `h-20`; она же записана в `phoneLayout.ts` для тех, кто обходит строку (тосты).
 */
export function BottomNav({ sections, active }: { sections: RailSection[]; active: RailId | null }) {
  const guardedClick = useGuardedNavClick();
  if (sections.length === 0) return null;

  return (
    <nav id="app-bottom-nav" data-testid="bottom-nav" aria-label="Разделы"
      className="md:hidden shrink-0 flex bg-surface border-t border-outline-variant pb-[env(safe-area-inset-bottom)]">
      {sections.map(section => {
        const on = section.def.id === active;
        return (
          <Link key={section.def.id} to={section.home} onClick={guardedClick(section.home)}
            aria-current={on ? 'true' : undefined}
            className="group flex-1 min-w-0 h-20 flex flex-col items-center justify-center gap-1 text-xs select-none focus-visible:outline-none">
            <SectionPill icon={section.def.icon} on={on} wide />
            <span className={`max-w-full px-1 truncate ${on ? 'font-semibold text-on-surface' : 'font-medium text-on-surface-variant'}`}>
              {section.def.label}
            </span>
          </Link>
        );
      })}
    </nav>
  );
}
