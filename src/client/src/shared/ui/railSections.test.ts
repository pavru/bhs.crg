import { describe, it, expect } from 'vitest';
import { railSections, settingsNav, workNav, type NavItem } from './navConfig';
import type { AccessInfo } from '@/shared/api/access';
import { visibleNav } from './navAccess';
import { activeRail, buildRail, groupItems, panelAt, railDef } from './railSections';

const labels = (items: NavItem[]) => items.map(i => i.label);
const all = [...workNav, ...settingsNav];

/** Рейл разделов и подпанель (issue #1429). */
describe('рейл разделов', () => {
  it('каждая страница стоит в разделе, который есть в рейле', () => {
    const ids = new Set(railSections.map(s => s.id));
    expect(all.filter(i => !ids.has(i.rail)).map(i => i.label)).toEqual([]);
  });

  it('пространством сужаются только рабочие разделы, «Система» — нет', () => {
    // «Система» — это `settingsNav`: его пространство не трогает (`useNavSections`). Пункт рабочего
    // списка в «Системе» или пункт администрирования в рабочем разделе разошёлся бы с этим молча.
    expect(workNav.filter(i => railDef(i.rail).unscoped).map(i => i.label)).toEqual([]);
    expect(settingsNav.filter(i => !railDef(i.rail).unscoped).map(i => i.label)).toEqual([]);
  });

  it('группы раздела идут одним куском, пункты без группы — над группами', () => {
    // Группа, разорванная другой, стала бы в подпанели двумя группами с одним именем.
    for (const def of railSections) {
      const items = all.filter(i => i.rail === def.id);
      const groups = groupItems(items).map(g => g.label);
      expect(new Set(groups).size, def.title).toBe(groups.length);
      expect(groups.slice(1).includes(undefined), def.title).toBe(false);
    }
  });

  it('раскладка подпанелей — как в задаче', () => {
    const rail = buildRail(workNav, settingsNav);
    const shape = Object.fromEntries(rail.map(s => [s.def.label,
      s.groups.map(g => [g.label ?? '', ...labels(g.items)])]));
    expect(shape).toEqual({
      'Стройки': [['', 'Стройки']],
      'Данные': [['', 'Общие данные', 'Наборы данных', 'Документы качества', 'Сверка']],
      'Затраты': [['', 'Счета', 'Накладные'],
        ['Отчёты и закрытие месяца', 'Реестр счетов', 'Затраты по стройке', 'Учётный период']],
      'Планы': [['', 'Сметы'], ['Справочники', 'Запомненные сопоставления']],
      'Система': [
        ['Документы', 'Типы документов', 'Шаблоны', 'Составные типы', 'Типы полей', 'Сопоставление полей',
          'Профили распознавания', 'Профили печати'],
        ['Люди и доступ', 'Пользователи', 'Роли и права'],
        ['Наблюдение', 'Журнал действий', 'Сообщения об ошибках'],
        ['Общее', 'Параметры системы'],
      ],
    });
  });

  it('раздел без открытых страниц в рейле не показывается, а ведёт он на первую свою', () => {
    const work = workNav.filter(i => i.to === '/waybills' || i.to === '/document-sets');
    const rail = buildRail(work, []);
    expect(rail.map(s => s.def.label)).toEqual(['Стройки', 'Затраты']);
    expect(rail[1].home).toBe('/waybills');
  });

  // Раздел модуля «Планирование» (plan-02, макет П0): последним рабочим, после «Затрат».
  it('раздел «Планы» — последний рабочий; без видимых пунктов его нет', () => {
    // Первый пункт раздела — «Сметы» (plan-09). Цепочка та же, что у оболочки: отбор по правам, потом рейл.
    // Без модуля пункт отсеивается, и раздел не строится; сломайся отбор или пропуск пустого раздела —
    // «Планы» появятся.
    const costsOnly: AccessInfo = {
      permissions: ['costs.invoice.read'], modules: [{ code: 'costs', title: 'Счета и накладные', available: true }],
    };
    expect(buildRail(visibleNav(workNav, costsOnly), []).map(s => s.def.id))
      .toEqual(['costs']);

    const rail = buildRail(workNav, settingsNav);
    expect(rail.map(s => s.def.label)).toEqual(['Стройки', 'Данные', 'Затраты', 'Планы', 'Система']);
    expect(railDef('plan').title).toBe('Планирование');
  });

  it('адрес принадлежит разделу по самому длинному пункту', () => {
    expect(activeRail('/tables/costs.invoices/registry', all)).toBe('costs');
    expect(activeRail('/invoices/42', all)).toBe('costs');
    expect(activeRail('/document-sets/c1/sets/s1', all)).toBe('constructions');
    expect(activeRail('/settings', all)).toBe('system');
    expect(activeRail('/profile', all)).toBeNull();
    // Похожий префикс — не тот же адрес.
    expect(activeRail('/datasets-archive', all)).toBeNull();
  });

  it('в карточке стройки подпанели нет — её место у навигации стройки', () => {
    const sites = railDef('constructions');
    expect(panelAt('/document-sets', sites)).toBe(true);
    expect(panelAt('/document-sets/c1', sites)).toBe(false);
    expect(panelAt('/invoices/42', railDef('costs'))).toBe(true);
  });
});
