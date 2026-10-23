import { describe, it, expect } from 'vitest';
import { CalendarRange, ClipboardList, Activity } from 'lucide-react';
import type { AccessInfo } from '@/shared/api/access';
import {
  constructionPanels, defaultPanel, panelPageOwners, panelRegistryProblems, panelsAt, type ConstructionPanel,
} from './constructionPanels';
import { workNav, type NavItem } from './navConfig';
import { inWorkspace, outsideItem, resolveWorkspace } from './workspaces';
import { pageOwners } from './navConfig';

const PLAN = { code: 'plan', title: 'Планирование', sections: ['constructions', 'catalog', 'progress'] };
const COSTS = { code: 'costs', title: 'Счета и накладные', sections: ['constructions', 'catalog', 'progress'] };
const ID = { code: 'id', title: 'Исполнительная документация', sections: ['constructions', 'catalog', 'datasets'] };

function access(permissions: string[], ...open: { code: string }[]): AccessInfo {
  return { permissions, modules: [PLAN, COSTS, ID].map(m => ({ ...m, available: open.some(o => o.code === m.code) })) };
}

// Панели блока «Работы» — тестовые: «Ход работ» вписывает plan-25, «Сметы» — plan-09, «График» — plan-20.
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
const registry = [progress, estimates, schedule, ...constructionPanels];

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
    // Настоящий реестр: блока «Работы» пока нет — у всех «Каталог», как до реестра.
    expect(defaultPanel('construction', estimator, insideOf(estimator))?.id).toBe('catalog');
    expect(defaultPanel('section', estimator, insideOf(estimator))?.id).toBe('catalog');
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
    const sections: ConstructionPanel = { ...constructionPanels[0], id: 'sections' };
    expect(panelRegistryProblems([...constructionPanels, sections])).toEqual(['панель «sections»: адрес занят соседним маршрутом']);
    const sets: ConstructionPanel = { ...constructionPanels[0], id: 'sets' };
    expect(panelRegistryProblems([...constructionPanels, sets])).toEqual(['панель «sets»: адрес занят соседним маршрутом']);
    expect(panelRegistryProblems([...constructionPanels, constructionPanels[0]])).toEqual(['панель «catalog» объявлена дважды']);
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

  it('пункты модуля в разных разделах рейла — ошибка: порядок модуля не определён', () => {
    const stray: NavItem = { ...planItem, to: '/prices', rail: 'costs' };
    expect(panelRegistryProblems([progress, estimates], [...nav, stray]))
      .toEqual(['пункты модуля «plan» стоят в разных разделах рейла: plan, costs']);
  });
});
