import { Link } from 'react-router';
import { useGuardedNavClick } from './NavigationGuard';
import type { RailId } from './navConfig';
import type { RailSection } from './railSections';

/**
 * Нижняя навигация телефона (issue #1434), по образцу MD3 navigation bar: рабочие разделы рейла —
 * значок и подпись под ним. Палец достаёт до низа экрана, а не до гамбургера в углу; разделов
 * немного, и прятать их за меню незачем.
 *
 * «Системы» здесь нет: администрирование на телефоне — пункт меню профиля (решение владельца от
 * 17.10.2026), а нижняя строка — для работы. Рабочих разделов нет вовсе — нет и строки: пустая
 * полоса у края звала бы нажать в никуда.
 */
export function BottomNav({ sections, active }: { sections: RailSection[]; active: RailId | null }) {
  const guardedClick = useGuardedNavClick();
  const work = sections.filter(s => !s.def.unscoped);
  if (work.length === 0) return null;

  return (
    <nav id="app-bottom-nav" data-testid="bottom-nav" aria-label="Разделы"
      className="md:hidden shrink-0 flex bg-surface border-t border-stroke pb-[env(safe-area-inset-bottom)]">
      {work.map(section => {
        const on = section.def.id === active;
        const Icon = section.def.icon;
        return (
          <Link key={section.def.id} to={section.home} onClick={guardedClick(section.home)}
            aria-current={on ? 'true' : undefined}
            className="group flex-1 min-w-0 h-20 flex flex-col items-center justify-center gap-1 text-xs select-none focus-visible:outline-none">
            <span className={'flex items-center justify-center w-16 h-8 rounded-full transition-colors ' +
              'group-focus-visible:ring-2 group-focus-visible:ring-brand ' +
              (on ? 'bg-tonal text-on-tonal' : 'text-fg3')}>
              <Icon size={22} />
            </span>
            <span className={`max-w-full px-1 truncate ${on ? 'font-semibold text-fg1' : 'font-medium text-fg2'}`}>
              {section.def.label}
            </span>
          </Link>
        );
      })}
    </nav>
  );
}
