import { useState, type ReactElement } from 'react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Link, useLocation } from 'react-router';
import { useGuardedNavClick } from './NavigationGuard';
import { useHistoryLayer } from './historyLayers';
import { MICRO_HEADING } from './panelLink';
import { ownerItem, type PanelGroup } from './railSections';
import type { SectionMenuValue } from './sectionMenuContext';

const item = 'flex items-center h-11 px-3 rounded-full text-sm select-none outline-none cursor-pointer state-layer';

/**
 * Меню страниц раздела — на телефоне вместо подпанели (issue #1434). Открывается заголовком страницы
 * («Счета на оплату ▾», см. `PageHeader`): заголовок и так называет, где человек, — к соседним
 * страницам ведёт тот же жест, а не отдельная кнопка, которую надо найти.
 *
 * Те же группы, что в подпанели, с теми же избранными стройками. Страницы вне пространства — группой
 * «Вне пространства» в конце, как в палитре: раскрывающаяся строка подпанели внутри меню была бы
 * меню в меню. Строки — 44 px: цель касания.
 *
 * Имя меню — имя кнопки, которая его открыла (Radix связывает их `aria-labelledby`).
 */
export function SectionMenu({ value, children }: { value: SectionMenuValue; children: ReactElement }) {
  const { groups, outside } = value;
  const { pathname } = useLocation();
  const guardedClick = useGuardedNavClick();
  const all: PanelGroup[] = outside.length ? [...groups, { label: 'Вне пространства', items: outside }] : groups;
  const here = ownerItem(pathname, all.flatMap(g => g.items))?.to;
  // «Назад» на телефоне закрывает меню, а не страницу (issue #1418).
  const [open, setOpen] = useState(false);
  useHistoryLayer(open, () => { setOpen(false); return true; });

  return (
    <DropdownMenu.Root open={open} onOpenChange={setOpen}>
      <DropdownMenu.Trigger asChild>{children}</DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="start" sideOffset={6} data-testid="section-menu"
          className="z-50 w-72 max-w-[calc(100vw-1rem)] max-h-[70vh] overflow-y-auto rounded-2xl border border-outline-variant bg-surface p-2 focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          {all.map((group, i) => (
            <DropdownMenu.Group key={group.label ?? i} className={i > 0 ? 'mt-2' : undefined}>
              {group.label && <DropdownMenu.Label className={`${MICRO_HEADING} pt-1`}>{group.label}</DropdownMenu.Label>}
              {group.items.map(({ to, label }) => (
                <DropdownMenu.Item key={to} asChild
                  className={item + (to === here ? ' bg-secondary-container text-on-secondary-container font-medium' : ' text-on-surface-variant')}>
                  <Link to={to} onClick={guardedClick(to)} aria-current={to === here ? 'page' : undefined}>
                    <span className="truncate">{label}</span>
                  </Link>
                </DropdownMenu.Item>
              ))}
            </DropdownMenu.Group>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
