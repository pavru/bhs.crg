import { Fragment, type ReactElement } from 'react';
import { useLocation } from 'react-router';
import { Menu, MenuGroup, MenuItem, MenuLabel, MenuSeparator } from './Menu';
import { ownerItem, type PanelGroup } from './railSections';
import type { SectionMenuValue } from './sectionMenuContext';

/**
 * Меню страниц раздела — на телефоне вместо подпанели (issue #1434). Открывается заголовком страницы
 * («Счета на оплату ▾», см. `PageHeader`): заголовок и так называет, где человек, — к соседним
 * страницам ведёт тот же жест, а не отдельная кнопка, которую надо найти.
 *
 * Те же группы, что в подпанели, с теми же избранными стройками. Страницы вне пространства — группой
 * «Вне пространства» в конце, как в палитре: раскрывающаяся строка подпанели внутри меню была бы
 * меню в меню. Текущая страница отмечена галочкой, без заливки ([UI-76]).
 *
 * Имя меню — имя кнопки, которая его открыла (Radix связывает их `aria-labelledby`).
 */
export function SectionMenu({ value, children }: { value: SectionMenuValue; children: ReactElement }) {
  const { groups, outside } = value;
  const { pathname } = useLocation();
  const all: PanelGroup[] = outside.length ? [...groups, { label: 'Вне пространства', items: outside }] : groups;
  const here = ownerItem(pathname, all.flatMap(g => g.items))?.to;

  return (
    <Menu trigger={children} sideOffset={6} width="w-72" data-testid="section-menu">
      {all.map((group, i) => (
        <Fragment key={group.label ?? i}>
          {i > 0 && <MenuSeparator />}
          <MenuGroup>
            {group.label && <MenuLabel>{group.label}</MenuLabel>}
            {group.items.map(({ to, label }) => (
              // Копия страницы (закреплённая, work-04) галочкой не отмечается: отмечена страница на своём месте.
              <MenuItem key={to} to={to} checked={to === here && !group.copies}
                current={to === here && !group.copies} description={group.notes?.[to]}>{label}</MenuItem>
            ))}
          </MenuGroup>
        </Fragment>
      ))}
    </Menu>
  );
}
