import { createContext, useContext } from 'react';
import type { NavItem, RailSectionDef } from './navConfig';
import type { PanelGroup } from './railSections';

/**
 * Страницы раздела для меню заголовка на телефоне (issue #1434): там подпанели нет, и её место
 * занимает заголовок страницы — «Счета на оплату ▾» раскрывает страницы раздела.
 *
 * Значение ставит оболочка, и только там, где на рабочем столе стояла бы подпанель (`panelAt`): в
 * карточке стройки навигацию ведёт сама карточка. На рабочем столе значения нет — там подпанель.
 */
export interface SectionMenuValue {
  def: RailSectionDef;
  groups: PanelGroup[];
  /** Страницы вне пространства — «Ещё N разделов» подпанели (у «Системы» их нет). */
  outside: NavItem[];
}

export const SectionMenuContext = createContext<SectionMenuValue | null>(null);

export function useSectionMenu(): SectionMenuValue | null {
  return useContext(SectionMenuContext);
}
