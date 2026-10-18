import type { NavItem, RailSectionDef } from './navConfig';
import type { PanelGroup, RailSection } from './railSections';
import type { SectionMenuValue } from './sectionMenuContext';

/**
 * Что оболочка добавляет на телефоне (issue #1434), и только на нём:
 * - `sectionMenu` — страницы раздела для меню заголовка, там, где на рабочем столе стояла бы
 *   подпанель (`panelSection`); у «Системы» без строки «вне пространства», как в подпанели;
 * - `admin` — раздел «Система» для пункта «Администрирование» меню профиля (решение владельца от
 *   17.10.2026). На рабочем столе «Система» в рейле, и меню профиля остаётся только про человека;
 * - `bottomNav` — рабочие разделы для нижней навигации, без «Системы».
 */
export function phoneChrome({ wide, panelSection, panelGroups, outside, sections }: {
  wide: boolean;
  panelSection: RailSectionDef | null;
  panelGroups: PanelGroup[];
  outside: NavItem[];
  sections: RailSection[];
}): { sectionMenu: SectionMenuValue | null; admin: RailSection | undefined; bottomNav: RailSection[] } {
  if (wide) return { sectionMenu: null, admin: undefined, bottomNav: [] };
  return {
    bottomNav: sections.filter(s => !s.def.unscoped),
    sectionMenu: panelSection
      ? { def: panelSection, groups: panelGroups, outside: panelSection.unscoped ? [] : outside }
      : null,
    admin: sections.find(s => s.def.unscoped),
  };
}
