import { describe, it, expect } from 'vitest';
import { CalendarRange, ClipboardList, Activity } from 'lucide-react';
import type { AccessInfo } from '@/shared/api/access';
import {
  constructionPanels, defaultPanel, panelPageOwners, panelRegistryProblems, panelsAt, type ConstructionPanel,
} from './constructionPanels';
import { workNav, type NavItem } from './navConfig';
import { inWorkspace, outsideItem, resolveWorkspace } from './workspaces';
import { visibleNav } from './navAccess';
import { pageOwners } from './navConfig';

const PLAN = { code: 'plan', title: 'Планирование', sections: ['constructions', 'catalog', 'progress'] };
const COSTS = { code: 'costs', title: 'Счета и накладные', sections: ['constructions', 'catalog', 'progress'] };
const ID = { code: 'id', title: 'Исполнительная документация', sections: ['constructions', 'catalog', 'datasets'] };

function access(permissions: string[], ...open: { code: string }[]): AccessInfo {
  return { permissions, modules: [PLAN, COSTS, ID].map(m => ({ ...m, available: open.some(o => o.code === m.code) })) };
}

// Панели блока «Работы» — тестовые: «Ход работ» вписывает plan-25, «График» — plan-20. «Сметы» здесь — тестовый
// вариант настоящих (plan-09): только у стройки, чтобы ветки уровня и адреса раздела было на чём проверить.
const progress: ConstructionPanel = {
  id: 'progress', label: 'Ход работ', icon: Activity, block: 'work', levels: ['construction', 'section'],
  section: 'progress', permission: ['plan.estimate.read', 'costs.report.read'],
};
const estimates: ConstructionPanel = {
  id: 'estimates', label: 'Сметы', icon: ClipboardList, block: 'work', levels: ['construction'],
  module: 'plan', permission: 'plan.estimate.read',
};
const schedule: ConstructionPanel = {
  id: 'schedule', label: 'График', icon: CalendarRange, block: 'work', levels: ['construction'],
  module: 'plan', permission: 'plan.schedule.read',
};
const core = constructionPanels.filter(p => !p.module);
const registry = [progress, estimates, schedule, ...core];
const catalog = constructionPanels.find(p => p.id === 'catalog')!;

// Пункт модуля в меню нужен правилу порядка: раздел рейла модуля берётся из его пунктов.
const planItem: NavItem = { to: '/estimates', label: 'Сметы', icon: ClipboardList, module: 'plan', permission: 'plan.estimate.read', rail: 'plan' };
const nav = [...workNav, planItem];

const ids = (list: { id: string }[]) => list.map(p => p.id);
const insideOf = (a: AccessInfo, choice = 'all') => {
  const ws = resolveWorkspace(a, [], choice).workspace;
  return (p: Pick<ConstructionPanel, 'module' | 'section'>) => inWorkspace(p, ws, a);
};

/** Панели карточки стройки и страницы раздела (plan-02, ТЗ AUTH-16.1, PLAN-20). */
describe('панели карточки стройки', () => {
  it('без модулей — прежние «Каталог», «Наборы данных», «Подписчики», блока «Работы» нет', () => {
    const plain = access(['core.constructions.read']);
    const { work, own } = panelsAt('construction', plain, insideOf(plain), undefined, registry);
    expect(ids(work)).toEqual([]);
    expect(ids(own)).toEqual(['catalog', 'datasets', 'subscribers']);
  });

  it('отбор по доступу: панель без права не показывается', () => {
    const accountant = access(['costs.report.read'], COSTS);
    const { work } = panelsAt('construction', accountant, insideOf(accountant), undefined, registry);
    // «Бухгалтер» видит только «Ход работ» — блок из одного пункта.
    expect(ids(work)).toEqual(['progress']);
  });

  it('отбор по уровню: панель, объявленная на обоих уровнях, есть и на странице раздела', () => {
    const manager = access(['plan.estimate.read', 'plan.schedule.read'], PLAN);
    expect(ids(panelsAt('construction', manager, insideOf(manager), undefined, registry).work)).toEqual(['progress', 'estimates', 'schedule']);
    expect(ids(panelsAt('section', manager, insideOf(manager), undefined, registry).work)).toEqual(['progress']);
  });

  it('отбор по пространству: в пространстве без модуля его панелей нет, открытая по ссылке — с пометкой', () => {
    const manager = access(['plan.estimate.read', 'plan.schedule.read', 'costs.report.read'], PLAN, COSTS);
    const costsOnly = insideOf(manager, 'module:costs');
    expect(ids(panelsAt('construction', manager, costsOnly, undefined, registry).work)).toEqual(['progress']);

    const opened = panelsAt('construction', manager, costsOnly, 'schedule', registry).work;
    expect(opened.map(p => [p.id, p.outside])).toEqual([['progress', false], ['schedule', true]]);
  });

  it('открытая по ссылке панель без права в навигацию не попадает', () => {
    const accountant = access(['costs.report.read'], COSTS);
    expect(ids(panelsAt('construction', accountant, insideOf(accountant), 'estimates', registry).work)).toEqual(['progress']);
  });

  it('панель по умолчанию — первая видимая: без модулей «Каталог», с панелью блока «Работы» — она', () => {
    const plain = access(['core.constructions.read']);
    expect(defaultPanel('construction', plain, insideOf(plain), registry)?.id).toBe('catalog');
    const estimator = access(['plan.estimate.read'], PLAN);
    expect(defaultPanel('construction', estimator, insideOf(estimator), registry)?.id).toBe('progress');
    // Настоящий реестр: у сметчика первой стоят «Сметы» (plan-09) — на обоих уровнях; без права — «Каталог».
    expect(defaultPanel('construction', estimator, insideOf(estimator))?.id).toBe('estimates');
    expect(defaultPanel('section', estimator, insideOf(estimator))?.id).toBe('estimates');
    expect(defaultPanel('construction', plain, insideOf(plain))?.id).toBe('catalog');
  });

  it('адрес панели модуля вне пространства получает плашку без ручной записи', () => {
    const manager = access(['plan.estimate.read', 'costs.report.read'], PLAN, COSTS);
    const costsOnly = resolveWorkspace(manager, [], 'module:costs').workspace;
    const owners = [...pageOwners, ...panelPageOwners(registry)];
    expect(outsideItem('/document-sets/c1/estimates', workNav, costsOnly, manager, owners)?.label).toBe('Сметы стройки');
    // Вложенный адрес панели («смета → редакция → строки») — её же.
    expect(outsideItem('/document-sets/c1/estimates/e1/rev/2', workNav, costsOnly, manager, owners)?.label).toBe('Сметы стройки');
    // «Сметы» объявлены только у стройки: тот же сегмент под разделом — не их адрес.
    expect(outsideItem('/document-sets/c1/sections/r1/estimates', workNav, costsOnly, manager, owners)).toBeNull();
    // Комплект с таким же хвостом — по-прежнему модуль ИД.
    expect(outsideItem('/document-sets/c1/sets/s1/estimates', workNav, costsOnly, manager, owners)?.label).toBe('Комплект документов');
  });
});

/** Правила реестра: PLAN-20, занятые адреса, порядок блока «Работы». */
describe('реестр панелей', () => {
  it('настоящий реестр правилам отвечает', () => {
    expect(panelRegistryProblems(constructionPanels)).toEqual([]);
    expect(panelRegistryProblems(registry, nav)).toEqual([]);
  });

  it('третья панель модуля — ошибка с названием модуля (уровни не в счёт)', () => {
    const third: ConstructionPanel = { ...schedule, id: 'prices', label: 'Цены' };
    expect(panelRegistryProblems([...registry, third], nav))
      .toEqual(['у модуля «plan» 3 панели карточки стройки (estimates, schedule, prices): PLAN-20 разрешает 2']);
  });

  it('адрес, занятый соседним маршрутом, и повтор — ошибки', () => {
    const sections: ConstructionPanel = { ...catalog, id: 'sections' };
    expect(panelRegistryProblems([...constructionPanels, sections])).toEqual(['панель «sections»: адрес занят соседним маршрутом']);
    const sets: ConstructionPanel = { ...catalog, id: 'sets' };
    expect(panelRegistryProblems([...constructionPanels, sets])).toEqual(['панель «sets»: адрес занят соседним маршрутом']);
    expect(panelRegistryProblems([...constructionPanels, catalog])).toEqual(['панель «catalog» объявлена дважды']);
  });

  it('порядок блока «Работы»: ядро раньше модулей, модуль подряд, модули — в порядке рейла', () => {
    expect(panelRegistryProblems([estimates, progress, schedule], nav)).toContain('в блоке «Работы» панель ядра стоит после панелей модулей');

    const invoices: ConstructionPanel = { ...estimates, id: 'invoices', module: 'costs', permission: 'costs.invoice.read' };
    expect(panelRegistryProblems([progress, estimates, invoices, schedule], nav))
      .toContain('в блоке «Работы» панели одного модуля стоят не подряд');
    // «Затраты» в рейле раньше «Планов» — и в блоке «Работы» обязаны стоять раньше.
    expect(panelRegistryProblems([progress, estimates, invoices], nav))
      .toEqual(['в блоке «Работы» модуль «costs» стоит после «plan», а в рейле — раньше']);
    expect(panelRegistryProblems([progress, invoices, estimates], nav)).toEqual([]);
  });

  it('панели модуля в блоке «Эта стройка» пунктов меню не требуют: порядок рейла им не нужен', () => {
    const notes: ConstructionPanel = { ...estimates, id: 'notes', block: 'own', module: 'id', permission: undefined };
    expect(panelRegistryProblems([...core, notes], workNav.filter(i => i.module !== 'id'))).toEqual([]);
    // Та же панель в блоке «Работы» — уже ошибка: модуль без пунктов не поставить по рейлу.
    expect(panelRegistryProblems([{ ...notes, block: 'work' }, ...core], workNav.filter(i => i.module !== 'id')))
      .toEqual(['у модуля «id» нет пунктов меню — порядок его панелей не определить']);
  });

  it('пункты модуля в разных разделах рейла — ошибка: порядок модуля не определён', () => {
    const stray: NavItem = { ...planItem, to: '/prices', rail: 'costs' };
    expect(panelRegistryProblems([progress, estimates], [...nav, stray]))
      .toEqual(['пункты модуля «plan» стоят в разных разделах рейла: plan, costs']);
  });
});

/** Настоящая панель «Сметы» (plan-09, задача 2.2, ТЗ PLAN-20): видна только с правом на сметы, на обоих уровнях. */
describe('панель «Сметы»', () => {
  it('без plan.estimate.read пункта «Сметы» нет ни у стройки, ни у раздела', () => {
    const accountant = access(['core.constructions.read', 'costs.report.read'], COSTS);
    for (const level of ['construction', 'section'] as const)
      expect(ids(panelsAt(level, accountant, insideOf(accountant)).work)).not.toContain('estimates');
    // Модуль открыт другим правом — пункта всё равно нет: ворота у него право, а не модуль.
    const scheduler = access(['plan.schedule.read'], PLAN);
    expect(ids(panelsAt('construction', scheduler, insideOf(scheduler)).work)).not.toContain('estimates');
  });

  it('с правом — «Сметы» в блоке «Работы» карточки стройки и страницы раздела', () => {
    const estimator = access(['plan.estimate.read'], PLAN);
    expect(ids(panelsAt('construction', estimator, insideOf(estimator)).work)).toEqual(['estimates']);
    expect(ids(panelsAt('section', estimator, insideOf(estimator)).work)).toEqual(['estimates']);
  });

  it('и пункт «Сметы» раздела «Планы» — тем же правом', () => {
    const item = workNav.find(i => i.to === '/estimates')!;
    expect(visibleNav([item], access(['core.constructions.read'], PLAN))).toEqual([]);
    expect(visibleNav([item], access(['plan.estimate.read'], PLAN))).toEqual([item]);
  });
});
