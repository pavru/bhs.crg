import { describe, expect, it } from 'vitest';
import { workNav, settingsNav } from './navConfig';
import { permissionsOf } from './navAccess';
import { RECORD_GROUPS } from './paletteRecords';
import { RECORD_ROUTES } from './recordRoutes';

/**
 * Перепись права экрана (shell-09, задача 3.5; Р-3; [UI-35.1] [AUTH-8]): у групп модуля `id` право экрана
 * записи проверяет и сервер — поставщик без него отвечает «скрыто» (`ScreenRights` в
 * `src/server/BHS.CRG.Api/Modules/IdRecordSearch.cs`). Список ниже выписан оттуда.
 *
 * Разошлись — красный тест: право экрана здесь берётся у раздела навигации (`navConfig`), которым закрыт и
 * маршрут. Сменили право раздела — поправьте `ScreenRights` на сервере (и этот список); сменили на
 * сервере — сверьте с разделом. Иначе сервер отдаёт карточки, которые клиент не откроет («не показано»
 * навсегда), или прячет те, что открылись бы.
 */
const SERVER_SCREEN_RIGHTS: Record<string, string> = {
  sets: 'core.constructions.read',
  templates: 'id.config.edit',
};

describe('право экрана групп «только карточки» — одно у клиента и сервера', () => {
  it.each(Object.entries(SERVER_SCREEN_RIGHTS))('группа %s', (code, serverRight) => {
    const group = RECORD_GROUPS.find(g => g.code === code);
    expect(group, `группы ${code} нет в RECORD_GROUPS`).toBeDefined();
    const route = RECORD_ROUTES[group!.recordType];
    const section = [...workNav, ...settingsNav].find(item => item.to === route.screen);
    expect(section, `у экрана ${route.screen} нет раздела навигации`).toBeDefined();
    expect(permissionsOf(section!),
      `право раздела ${route.screen} разошлось с ScreenRights["${code}"] в IdRecordSearch.cs — правьте отстающую сторону`)
      .toEqual([serverRight]);
  });
});
