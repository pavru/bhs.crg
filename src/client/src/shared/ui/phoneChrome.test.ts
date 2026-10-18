import { describe, expect, it } from 'vitest';
import { phoneChrome } from './phoneChrome';
import { railDef, type RailSection } from './railSections';

/**
 * Что оболочка добавляет только на телефоне (issue #1434, ревью PR #1488): меню раздела в заголовке
 * и «Администрирование» в меню профиля. На рабочем столе — ни того ни другого: там подпанель и рейл.
 */

const page = (to: string, rail: RailSection['def']['id']) => ({ to, label: to, icon: railDef(rail).icon, rail });
const costs = railDef('costs');
const system = railDef('system');
const groups = [{ items: [page('/invoices', 'costs')] }];
const outside = [page('/datasets', 'data')];
const sections: RailSection[] = [
  { def: costs, groups, home: '/invoices' },
  { def: system, groups: [{ items: [page('/users', 'system')] }], home: '/users' },
];

describe('оболочка телефона', () => {
  it('на рабочем столе ничего не добавляет', () => {
    expect(phoneChrome({ wide: true, panelSection: costs, panelGroups: groups, outside, sections }))
      .toEqual({ sectionMenu: null, admin: undefined, bottomNav: [] });
  });

  it('на телефоне — меню раздела с его страницами и «вне пространства», и «Система» для меню профиля', () => {
    const { sectionMenu, admin, bottomNav } = phoneChrome({ wide: false, panelSection: costs, panelGroups: groups, outside, sections });
    expect(sectionMenu).toEqual({ def: costs, groups, outside });
    expect(admin?.def.id).toBe('system');
    // Внизу — только рабочие разделы.
    expect(bottomNav.map(s => s.def.id)).toEqual(['costs']);
  });

  it('у «Системы» строки «вне пространства» нет — как в подпанели', () => {
    const { sectionMenu } = phoneChrome({ wide: false, panelSection: system, panelGroups: groups, outside, sections });
    expect(sectionMenu?.outside).toEqual([]);
  });

  it('где подпанели нет (карточка стройки), нет и меню', () => {
    expect(phoneChrome({ wide: false, panelSection: null, panelGroups: [], outside, sections }).sectionMenu).toBeNull();
  });

  it('без открытых страниц администрирования пункта нет', () => {
    expect(phoneChrome({ wide: false, panelSection: costs, panelGroups: groups, outside, sections: [sections[0]] }).admin)
      .toBeUndefined();
  });
});
