import { Database, Table2, Users } from 'lucide-react';
import type { AccessInfo } from '@/shared/api/access';
import { allowed } from './navAccess';
import { pageOwners, railSections, workNav, type NavItem, type PageOwner, type RailId } from './navConfig';

/**
 * Панели карточки стройки и страницы раздела (plan-02, ТЗ AUTH-16.1, PLAN-20) — общий механизм, через
 * который модуль добавляет свою панель («Сметы», «График»), а ядро — свои («Каталог», «Ход работ»).
 *
 * <p>Раньше карточка и страница раздела перечисляли три панели руками, каждая свои. Модулю пришлось бы
 * править обе страницы, отбор по правам и по пространству повторять в каждой, а адрес панели
 * дописывать в `pageOwners` — забытая запись значила бы, что плашки «вне пространства» у панели нет.
 * Теперь всё это следует из одной записи реестра.</p>
 *
 * <p>Здесь — только описание и правила, без отрисовки: реестр читают навигация, пространства и тесты,
 * и экраны панелей им не нужны (а замкнули бы импорт на самих себя). Что рисует панель — карта
 * `panelViews` рядом со страницами стройки; тест требует, чтобы у каждой записи было своё содержимое.</p>
 *
 * Порядок записей — порядок показа, как в `navConfig.ts`.
 */

export type PanelLevel = 'construction' | 'section';

/** «Работы» — над разделами; «Эта стройка» («Этот раздел») — под ними (макет П0, вариант 2). */
export type PanelBlock = 'work' | 'own';

export interface ConstructionPanel {
  /** Сегмент адреса: `/document-sets/{id}/{panel}` и `/document-sets/{id}/sections/{sid}/{panel}`. */
  id: string;
  label: string;
  icon: typeof Database;
  block: PanelBlock;
  /**
   * Уровни, где панель есть. Множество, а не одно значение: «Наборы данных» и «Ход работ» живут и в
   * карточке стройки, и на странице раздела, и для `PLAN-20` это одна панель.
   */
  levels: PanelLevel[];
  /** Модуль панели — она в пространстве, если модуль в нём (правило `inWorkspace`). */
  module?: string;
  /** Раздел ядра панели ядра; без него панель ядра видна в любом пространстве. */
  section?: string;
  /** Право или «любое из» — как у пункта навигации. */
  permission?: string | string[];
}

const BOTH: PanelLevel[] = ['construction', 'section'];

export const constructionPanels: ConstructionPanel[] = [
  { id: 'catalog', label: 'Каталог', icon: Database, block: 'own', levels: BOTH },
  // Наборы данных — раздел ядра `datasets` (ревью PR #1330): в пространстве, которое его не объявило,
  // пункта нет, как и в главном меню; открытая по ссылке панель получает плашку.
  { id: 'datasets', label: 'Наборы данных', icon: Table2, block: 'own', levels: BOTH, section: 'datasets' },
  { id: 'subscribers', label: 'Подписчики', icon: Users, block: 'own', levels: BOTH },
];

/** Адреса, занятые соседними маршрутами карточки стройки: панель с таким `id` их бы перекрыла. */
const RESERVED_IDS = ['sections', 'sets'];

/** У модуля не больше двух панелей карточки стройки (PLAN-20) — считаются `id`, а не пары с уровнем. */
const MODULE_PANELS_MAX = 2;

/** Входит ли панель в пространство — то же правило, что у пункта навигации (`inWorkspace`). */
export type PanelInside = (panel: Pick<ConstructionPanel, 'module' | 'section'>) => boolean;

export interface ShownPanel extends ConstructionPanel {
  /** Открыта по ссылке вне пространства: стоит в навигации одна из скрытых, с пометкой. */
  outside: boolean;
}

/**
 * Панели уровня, доступные человеку, по блокам. Панель вне пространства в навигацию не попадает —
 * кроме открытой (`active`): пришедший по ссылке видит, где он, и пометку «вне пространства».
 */
export function panelsAt(level: PanelLevel, access: AccessInfo, inside: PanelInside, active?: string,
  panels: ConstructionPanel[] = constructionPanels): { work: ShownPanel[]; own: ShownPanel[] } {
  const shown = panels
    .filter(p => p.levels.includes(level) && allowed(p, access) && (inside(p) || p.id === active))
    .map(p => ({ ...p, outside: !inside(p) }));
  return { work: shown.filter(p => p.block === 'work'), own: shown.filter(p => p.block === 'own') };
}

/**
 * Панель по умолчанию — первая видимая (макет П0): сверху вниз, блок «Работы», затем «Эта стройка».
 * У человека без модулей это «Каталог», как было до реестра.
 */
export function defaultPanel(level: PanelLevel, access: AccessInfo, inside: PanelInside,
  panels: ConstructionPanel[] = constructionPanels): ConstructionPanel | undefined {
  const { work, own } = panelsAt(level, access, inside, undefined, panels);
  return work[0] ?? own[0];
}

/** Панель уровня по сегменту адреса — независимо от доступа: недоступную страница объясняет сама. */
export function panelAt(level: PanelLevel, id: string | undefined,
  panels: ConstructionPanel[] = constructionPanels): ConstructionPanel | undefined {
  return panels.find(p => p.id === id && p.levels.includes(level));
}

/**
 * Записи «чей это адрес» для панелей модулей и разделов ядра — плашка «вне пространства» у панели
 * следует из её записи в реестре, а не из второй, ручной.
 */
export function panelPageOwners(panels: ConstructionPanel[] = constructionPanels): PageOwner[] {
  return panels.filter(p => p.module || p.section).map(p => {
    const sectionPart = p.levels.includes('construction') ? '(\\/sections\\/[^/]+)?' : '\\/sections\\/[^/]+';
    const prefix = p.levels.includes('section') ? sectionPart : '';
    return {
      match: new RegExp(`^\\/document-sets\\/[^/]+${prefix}\\/${p.id}(\\/|$)`),
      label: `${p.label} стройки`,
      module: p.module,
      section: p.section,
    };
  });
}

/** Все владельцы адресов под «Стройками»: комплект и прочие из `navConfig`, затем панели. */
export const allPageOwners: PageOwner[] = [...pageOwners, ...panelPageOwners()];

/**
 * Что в реестре нарушает правила (PLAN-20 и соседние маршруты). Пустой список — всё в порядке.
 *
 * Порядок блока «Работы»: панели ядра раньше панелей модулей, панели одного модуля подряд, модули — в
 * порядке рейла. Раздел рейла модуля берётся из его пунктов меню, поэтому заодно все пункты модуля
 * обязаны стоять в одном разделе: иначе «порядок рейла» для модуля не определён.
 */
export function panelRegistryProblems(panels: ConstructionPanel[], nav: NavItem[] = workNav,
  rails: RailId[] = railSections.map(r => r.id)): string[] {
  const problems: string[] = [];

  const ids = panels.map(p => p.id);
  for (const id of new Set(ids)) {
    if (ids.filter(x => x === id).length > 1) problems.push(`панель «${id}» объявлена дважды`);
    if (RESERVED_IDS.includes(id)) problems.push(`панель «${id}»: адрес занят соседним маршрутом`);
  }

  for (const p of panels.filter(p => p.module && p.section))
    problems.push(`панель «${p.id}» называет и модуль, и раздел ядра`);

  const modules = [...new Set(panels.flatMap(p => (p.module ? [p.module] : [])))];
  for (const module of modules) {
    const own = new Set(panels.filter(p => p.module === module).map(p => p.id));
    if (own.size > MODULE_PANELS_MAX)
      problems.push(`у модуля «${module}» ${own.size} панели карточки стройки (${[...own].join(', ')}): PLAN-20 разрешает ${MODULE_PANELS_MAX}`);
  }

  const railOf = new Map<string, number>();
  for (const module of modules) {
    const itemRails = [...new Set(nav.filter(i => i.module === module).map(i => i.rail))];
    if (itemRails.length === 0) problems.push(`у модуля «${module}» нет пунктов меню — порядок его панелей не определить`);
    else if (itemRails.length > 1) problems.push(`пункты модуля «${module}» стоят в разных разделах рейла: ${itemRails.join(', ')}`);
    else railOf.set(module, rails.indexOf(itemRails[0]));
  }

  const work = panels.filter(p => p.block === 'work');
  const firstModule = work.findIndex(p => p.module);
  if (firstModule >= 0 && work.slice(firstModule).some(p => !p.module))
    problems.push('в блоке «Работы» панель ядра стоит после панелей модулей');
  const runs = work.filter(p => p.module).map(p => p.module!).filter((m, i, all) => i === 0 || all[i - 1] !== m);
  if (new Set(runs).size !== runs.length) problems.push('в блоке «Работы» панели одного модуля стоят не подряд');
  for (let i = 1; i < runs.length; i++) {
    const [a, b] = [railOf.get(runs[i - 1]), railOf.get(runs[i])];
    if (a !== undefined && b !== undefined && a > b)
      problems.push(`в блоке «Работы» модуль «${runs[i]}» стоит после «${runs[i - 1]}», а в рейле — раньше`);
  }
  return problems;
}
