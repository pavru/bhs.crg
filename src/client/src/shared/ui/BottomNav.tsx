import { useState, type ReactNode } from 'react';
import { Link, useLocation } from 'react-router';
import { BottomSheet } from './BottomSheet';
import { Icon } from './Icon';
import { Apps, Keep, type FilledIconDef } from './icons';
import { useGuardedNavClick } from './NavigationGuard';
import { PageList } from './PageList';
import { PinnedLink } from './PinnedGroup';
import { PINNED_GROUP_LABEL, type PinnedEntry } from './pinnedSections';
import type { RailId } from './navConfig';
import { ownerItem, type RailSection } from './railSections';
import { SectionPill } from './SectionRail';

/** Подпись третьего места и имя листа: слово «раздел» в навигации не используется (Р0). */
export const MENU_LABEL = 'Меню';

/** Подсказка пустого листа «Закреплённые»: как закрепить — на телефоне это «⋮» строки в «Меню» (work-04). */
export const PINNED_EMPTY_HINT = 'Закрепите страницу в меню «⋮» её строки — в листе «Меню»';

/**
 * Пустой лист «Меню»: рабочих разделов, кроме Главной, у человека нет. Место строки при этом остаётся —
 * строка у всех одна (Р0), — а лист говорит, почему в нём пусто, вместо того чтобы молчать.
 */
export const MENU_EMPTY_HINT = 'Других разделов у вас пока нет: их открывают права, которые выдаёт администратор';

type Place = 'home' | 'pinned' | 'menu';

/**
 * Нижняя навигация телефона (work-03, макет Р0, телефон — вариант Б): у всех три постоянных места —
 * «Главная · Закреплённые · Меню». Строка не зависит от числа модулей: шестой и седьмой разделы её не
 * ломают, а прежняя строка разделов при шести рабочих разделах выходила за предел MD3 в пять мест.
 *
 * - «Главная» — ссылка на `/home` (shell-08).
 * - «Закреплённые» — лист со списком закреплений (work-04), в их порядке; пусто — подсказка, как закрепить.
 * - «Меню» — лист со всеми рабочими разделами и их страницами (вместо drawer); у строки страницы — «⋮» с
 *   «Закрепить страницу», как у пункта подпанели на рабочем столе. «Администрирования» в листе нет: на
 *   телефоне оно — пункт меню профиля (решение владельца от 17.10.2026).
 *
 * Отмечено место, где человек сейчас: Главная — на Главной, «Закреплённые» — на закреплённой странице,
 * «Меню» — на странице раздела из листа. Вне разделов листа (профиль, «Система» из меню профиля) не
 * отмечено ничего: отметка звала бы туда, где текущей страницы нет.
 *
 * Лист открыт на той записи истории, где его открыли: любой переход (выбор страницы, «Главная») его
 * закрывает, а переход, отменённый охраной ухода, — нет: человек, ответивший «Остаться», видит лист, из
 * которого выбирал. Поэтому закрытие — по смене записи (`location.key`), а не по клику на ссылку.
 *
 * Разделы отбирает `phoneChrome`: рабочие, без «Системы». Разделов нет — нет и строки: пустая полоса у
 * края звала бы нажать в никуда. Высота — `h-20`; она же записана в `phoneLayout.ts` для тех, кто обходит
 * строку (тосты). Поверхность — `surface-container` без черты (sharedui-md3-15); подпись — `label-medium`
 * одним весом у всех, выбранное место отличают индикатор и залитый знак.
 */
export function BottomNav({ sections, active, pinned = [], pinnable = () => false }: {
  sections: RailSection[];
  active: RailId | null;
  /** Закреплённые страницы, уже отобранные по правам (`pinnedGroup`). */
  pinned?: PinnedEntry[];
  /** Страница ли это навигации — её можно закрепить; избранная стройка — запись, а не страница. */
  pinnable?: (to: string) => boolean;
}) {
  const guardedClick = useGuardedNavClick();
  const { pathname, key } = useLocation();
  const [sheet, setSheet] = useState<'pinned' | 'menu' | null>(null);
  // Сброс при смене записи — в рендере, а не эффектом: закоммиченный кадр уже без листа.
  const [openedAt, setOpenedAt] = useState(key);
  if (openedAt !== key) {
    setOpenedAt(key);
    if (sheet) setSheet(null);
  }
  if (sections.length === 0) return null;

  const home = sections.find(s => s.def.id === 'home');
  const menu = sections.filter(s => s.def.id !== 'home');
  const pinnedHere = !!ownerItem(pathname, pinned.map(e => e.item));
  const inMenu = menu.some(s => s.def.id === active);
  const here: Place | null = active === 'home' ? 'home' : pinnedHere ? 'pinned' : inMenu ? 'menu' : null;
  const close = () => setSheet(null);

  return (
    <>
      <nav id="app-bottom-nav" data-testid="bottom-nav" aria-label="Разделы"
        className="md:hidden shrink-0 flex bg-surface-container pb-[env(safe-area-inset-bottom)]">
        {home && (
          <Link to={home.home} onClick={guardedClick(home.home)} aria-current={here === 'home' ? 'true' : undefined}
            className={PLACE}>
            <PlaceLabel icon={home.def.icon} on={here === 'home'}>{home.def.label}</PlaceLabel>
          </Link>
        )}
        <button type="button" onClick={() => setSheet('pinned')} aria-haspopup="dialog" aria-expanded={sheet === 'pinned'}
          aria-current={here === 'pinned' ? 'true' : undefined} className={PLACE}>
          <PlaceLabel icon={Keep} on={here === 'pinned'}>{PINNED_GROUP_LABEL}</PlaceLabel>
        </button>
        <button type="button" onClick={() => setSheet('menu')} aria-haspopup="dialog" aria-expanded={sheet === 'menu'}
          aria-current={here === 'menu' ? 'true' : undefined} className={PLACE}>
          <PlaceLabel icon={Apps} on={here === 'menu'}>{MENU_LABEL}</PlaceLabel>
        </button>
      </nav>

      <BottomSheet open={sheet === 'pinned'} onClose={close} title={PINNED_GROUP_LABEL}>
        <div data-testid="pinned-sheet">
          {pinned.length
            ? <div className="space-y-0.5">{pinned.map(entry => <PinnedLink key={entry.item.to} entry={entry} />)}</div>
            : <p className="py-4 text-body-medium text-on-surface-variant">{PINNED_EMPTY_HINT}</p>}
        </div>
      </BottomSheet>

      <BottomSheet open={sheet === 'menu'} onClose={close} title={MENU_LABEL}>
        <div data-testid="menu-sheet" className="space-y-6">
          {!menu.length && <p className="py-4 text-body-medium text-on-surface-variant">{MENU_EMPTY_HINT}</p>}
          {menu.map(section => (
            <section key={section.def.id} aria-labelledby={`menu-sheet-${section.def.id}`}>
              {/* Подпись — как в рейле рабочего стола (Р0): один и тот же раздел зовётся одним словом. */}
              <h3 id={`menu-sheet-${section.def.id}`} className="flex items-center gap-3 px-4 mb-2 text-title-medium text-on-surface">
                <Icon icon={section.def.icon} size={24} className="shrink-0 text-on-surface-variant" />
                {section.def.label}
              </h3>
              <PageList groups={section.groups} pinnable={pinnable} />
            </section>
          ))}
        </div>
      </BottomSheet>
    </>
  );
}

const PLACE = 'group flex-1 min-w-0 h-20 flex flex-col items-center justify-center gap-1 text-label-medium select-none focus-visible:outline-none';

function PlaceLabel({ icon, on, children }: { icon: FilledIconDef; on: boolean; children: ReactNode }) {
  return (
    <>
      <SectionPill icon={icon} on={on} wide />
      <span className={`max-w-full px-1 truncate ${on ? 'text-on-surface' : 'text-on-surface-variant'}`}>{children}</span>
    </>
  );
}
