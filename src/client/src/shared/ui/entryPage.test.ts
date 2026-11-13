import { describe, it, expect } from 'vitest';
import { Assignment, Engineering } from './icons';
import type { AccessInfo } from '@/shared/api/access';
import { HOME_ENTRY_RANK, entryPage, entryRankProblems, isHomeItem } from './entryPage';
import { WORK_ENTRY_RANK, settingsNav, workNav, type NavItem } from './navConfig';
import { visibleNav } from './navAccess';
import { resolveWorkspace, splitNav, type Workspace } from './workspaces';

const PLAN = { code: 'plan', title: 'Планирование', sections: ['constructions', 'catalog'] };
const COSTS = { code: 'costs', title: 'Счета и накладные', sections: ['constructions', 'catalog', 'periods'] };
const WORK = { code: 'work', title: 'Учёт работ', sections: ['constructions'] };

/** Модули экземпляра — все три; открыты те, что названы. */
function access(permissions: string[], ...open: { code: string }[]): AccessInfo {
  return {
    permissions,
    modules: [PLAN, COSTS, WORK].map(m => ({ ...m, available: open.some(o => o.code === m.code) })),
  };
}

const all = (a: AccessInfo) => resolveWorkspace(a, [], 'all').workspace;

/** Вход так, как его считает оболочка: из показанного меню (`useNavSections`) и всех объявленных. */
const enter = (items: NavItem[], ws: Workspace, a: AccessInfo) =>
  entryPage(splitNav(visibleNav(items, a), ws, a).inside, a, items);

// Пункты модулей — тестовые. «Сметы» повторяют пункт plan-09; пункты «Работ» заводят change экранов
// (work-21…work-23, work-12) с рангом из `WORK_ENTRY_RANK` (work-03), адреса здесь условные.
const estimates: NavItem = { to: '/estimates', label: 'Сметы', icon: Assignment, module: 'plan', permission: 'plan.estimate.read', rail: 'plan' };
const myReports: NavItem = { to: '/my-reports', label: 'Мои отчёты', icon: Engineering, module: 'work',
  permission: 'work.report.own', rail: 'work', entry: WORK_ENTRY_RANK.myReports };
const review: NavItem = { to: '/work-review', label: 'Приёмка', icon: Engineering, module: 'work',
  permission: 'work.report.review', rail: 'work', entry: WORK_ENTRY_RANK.review };
const summary: NavItem = { to: '/work-summary', label: 'Сводка', icon: Engineering, module: 'work',
  permission: ['work.report.review', 'work.report.read'], rail: 'work' };
const assignments: NavItem = { to: '/assignments', label: 'Назначения', icon: Engineering, module: 'work',
  permission: 'work.assignment.edit', rail: 'work' };
// Пункт Главной — из меню (shell-08): пункт ядра без права с рангом Главной.
const home = workNav.find(isHomeItem)!;

const nav = [...workNav.filter(i => !isHomeItem(i)), estimates, myReports, ...settingsNav];
const withHome = [home, ...nav];

/** Страница входа (plan-02, ТЗ AUTH-16.1, AUTH-15). */
describe('страница входа', () => {
  it('роль с правами модуля без «Строек» входит в модуль, а не в отказ на «Стройках»', () => {
    const estimator = access(['plan.estimate.read'], PLAN);
    expect(enter(nav, all(estimator), estimator)).toBe('/estimates');
    // С Главной — Главная: у «Смет» ранга ниже неё нет (П0).
    expect(enter(withHome, all(estimator), estimator)).toBe('/home');
  });

  it('по умолчанию — первый видимый пункт: у всех, кто видит стройки, это «Стройки»', () => {
    const clerk = access(['core.constructions.read', 'costs.invoice.read'], COSTS);
    expect(enter(nav, all(clerk), clerk)).toBe('/document-sets');
  });

  it('видимых пунктов нет — никуда: страница «нет доступа», а не раздел с отказом', () => {
    const nobody = access([]);
    expect(enter(nav, all(nobody), nobody)).toBeNull();
  });

  it('кроме Главной, ничего не видно — тоже никуда: Главная не работа (AUTH-15)', () => {
    const nobody = access([]);
    expect(enter(withHome, all(nobody), nobody)).toBeNull();
    // Одно право ядра — уже работа, и вход ведёт на Главную.
    const reader = access(['core.catalog.read']);
    expect(enter(withHome, all(reader), reader)).toBe('/home');
  });

  it('Главная — первый пункт меню, в разделе «Главная», ранга Главной', () => {
    expect(workNav[0]).toMatchObject({ to: '/home', rail: 'home', entry: HOME_ENTRY_RANK });
    expect(workNav[0].permission).toBeUndefined();
    expect(workNav[0].module).toBeUndefined();
  });

  it('пункт входа вне пространства — страница входа этого пространства, без смены пространства', () => {
    const fitter = access(['core.constructions.read', 'work.report.own', 'costs.invoice.read'], WORK, COSTS);
    const costsOnly = resolveWorkspace(fitter, [], 'module:costs').workspace;
    // Без Главной «Мои отчёты» с рангом открылись бы везде, где видны; в пространстве «Счета» их нет.
    expect(enter(nav, all(fitter), fitter)).toBe('/my-reports');
    expect(enter(nav, costsOnly, fitter)).toBe('/document-sets');
  });

  it('Главной нет — наименьший ранг, правило одного модуля не действует', () => {
    const admin = access(['core.constructions.read', 'plan.estimate.read', 'work.report.own'], PLAN, WORK);
    expect(enter(nav, all(admin), admin)).toBe('/my-reports');
  });

  it('при равных рангах в одном разделе рейла — пункт, стоящий в меню раньше', () => {
    const a: NavItem = { ...estimates, entry: 50 };
    const b: NavItem = { ...myReports, rail: 'plan', entry: 50 };
    const both = access(['plan.estimate.read', 'work.report.own'], PLAN, WORK);
    expect(enter([a, b], all(both), both)).toBe('/estimates');
    expect(enter([b, a], all(both), both)).toBe('/my-reports');
  });

  it('при равных рангах в разных разделах — порядок рейла, а не порядок массива', () => {
    // «Затраты» в рейле раньше «Планов»: массив меню по рейлу не упорядочен (`/periods` до `/reconciliations`).
    const plan: NavItem = { ...estimates, entry: 50 };
    const costs: NavItem = { ...myReports, to: '/costs-entry', rail: 'costs', entry: 50 };
    const both = access(['plan.estimate.read', 'work.report.own'], PLAN, WORK);
    expect(enter([plan, costs], all(both), both)).toBe('/costs-entry');
  });
});

/** Главная в расчёте (решение владельца 22.10.2026, вариант Б `shell-08-home`). */
describe('страница входа и Главная', () => {
  it('ранг ниже Главной при одном доступном модуле — открывается пункт, а не Главная', () => {
    const fitter = access(['core.constructions.read', 'work.report.own'], WORK);
    expect(enter(withHome, all(fitter), fitter)).toBe('/my-reports');
  });

  it('два доступных модуля — Главная, даже при ранге ниже её', () => {
    const manager = access(['core.constructions.read', 'work.report.own', 'plan.estimate.read'], WORK, PLAN);
    expect(enter(withHome, all(manager), manager)).toBe('/home');
  });

  it('модули считаются по аккаунту, а не по пространству', () => {
    const manager = access(['core.constructions.read', 'work.report.own', 'plan.estimate.read'], WORK, PLAN);
    const workOnly = resolveWorkspace(manager, [], 'module:work').workspace;
    expect(enter(withHome, workOnly, manager)).toBe('/home');
  });

  it('Главная, которой человеку не видно, всё равно объявлена: правило одного модуля действует', () => {
    const closedHome: NavItem = { ...home, permission: 'core.home.read' };
    const manager = access(['core.constructions.read', 'work.report.own', 'plan.estimate.read'], WORK, PLAN);
    // Без Главной в видимом «Мои отчёты» с рангом ниже неё не открываются: модулей два.
    expect(enter([closedHome, ...nav], all(manager), manager)).toBe('/document-sets');
  });

  it('все права — Главная', () => {
    const admin = access(['core.constructions.read', 'work.report.own', 'plan.estimate.read', 'costs.invoice.read'], WORK, PLAN, COSTS);
    expect(enter(withHome, all(admin), admin)).toBe('/home');
  });

  it('ранг больше ранга Главной — Главная', () => {
    const late: NavItem = { ...myReports, entry: HOME_ENTRY_RANK + 10 };
    const fitter = access(['work.report.own'], WORK);
    expect(enter([home, late], all(fitter), fitter)).toBe('/home');
    // Без Главной его ранг решает среди остальных.
    expect(enter([estimates, late], all(fitter), fitter)).toBe('/my-reports');
  });
});

/** Ранги пунктов меню (П0, Архитектор): пункт ядра ниже Главной уводил бы от неё всех. */
describe('ранги входа в меню', () => {
  it('у пунктов меню нет ранга ядра ниже Главной', () => {
    expect(entryRankProblems([...workNav, ...settingsNav])).toEqual([]);
  });

  it('две Главные и пункт по адресу «/» — ошибки', () => {
    expect(entryRankProblems([home, { ...home, to: '/dashboard', label: 'Сводка' }]))
      .toEqual([`ранг Главной (${HOME_ENTRY_RANK}) у нескольких пунктов ядра: «Главная», «Сводка»`]);
    expect(entryRankProblems([{ ...home, to: '/' }]))
      .toEqual(['«Главная»: адрес «/» — страница входа, пункт там не откроется']);
  });

  it('пункт ядра с рангом ниже Главной — ошибка с его названием', () => {
    const early: NavItem = { ...workNav.find(i => i.to === '/document-sets')!, entry: 1 };
    expect(entryRankProblems([early, myReports])).toEqual([
      `«Стройки»: пункт ядра с рангом входа 1 ниже Главной (${HOME_ENTRY_RANK})`,
    ]);
  });
});

/**
 * Страница входа модуля «Учёт работ» (work-03, ТЗ AUTH-16.1, WORK-2; макет Р0). Пункты — по Р0: «Мои
 * отчёты», «Приёмка», «Сводка», «Назначения»; ранги — из `WORK_ENTRY_RANK`.
 */
describe('страница входа модуля «Учёт работ»', () => {
  const workPages = [myReports, review, summary, assignments];
  const menu = [home, ...workNav.filter(i => !isHomeItem(i)), estimates, ...workPages, ...settingsNav];
  const reads = ['core.types.read', 'core.catalog.read', 'core.constructions.read', 'core.datasets.read'];

  it('ранги «Работ» — меньше Главной, «Мои отчёты» раньше «Приёмки»', () => {
    expect(WORK_ENTRY_RANK.myReports).toBeLessThan(WORK_ENTRY_RANK.review);
    expect(WORK_ENTRY_RANK.review).toBeLessThan(HOME_ENTRY_RANK);
    expect(entryRankProblems(menu)).toEqual([]);
  });

  it('монтажник — «Мои отчёты», а не Главная и не «Стройки»', () => {
    const fitter = access(['work.report.own', 'core.files.use'], WORK);
    expect(enter(menu, all(fitter), fitter)).toBe('/my-reports');
    // Со справочными правами ядра — то же: «Стройки» стоят в меню раньше, но ранга у них нет.
    const withReads = access(['work.report.own', ...reads], WORK);
    expect(enter(menu, all(withReads), withReads)).toBe('/my-reports');
  });

  it('один модуль без своих отчётов, с приёмкой — «Приёмка»; со своими отчётами — «Мои отчёты»', () => {
    const reviewer = access(['work.report.review', 'work.assignment.edit', ...reads], WORK);
    expect(enter(menu, all(reviewer), reviewer)).toBe('/work-review');
    const both = access(['work.report.own', 'work.report.review', ...reads], WORK);
    expect(enter(menu, all(both), both)).toBe('/my-reports');
  });

  it('видны только «Сводка» и «Назначения» — Главная: ранга у них нет', () => {
    const reader = access(['work.report.read', 'work.assignment.edit', ...reads], WORK);
    expect(enter(menu, all(reader), reader)).toBe('/home');
  });

  it('«Администратор» и «Менеджер проекта» — Главная: модулей больше одного', () => {
    const admin = access(['work.report.own', 'work.report.review', 'work.assignment.edit', 'work.report.read',
      'plan.estimate.read', 'costs.invoice.read', ...reads], WORK, PLAN, COSTS);
    expect(enter(menu, all(admin), admin)).toBe('/home');
    const manager = access(['work.report.review', 'work.assignment.edit', 'plan.estimate.read', ...reads],
      WORK, PLAN, COSTS);
    expect(enter(menu, all(manager), manager)).toBe('/home');
  });

  it('одно право контракта `work.facts.read` — вход как без модуля', () => {
    // Модуль, открытый одним правом контракта, приходит с `available: false` (plan-21) — пункты «Работ»
    // не видны, и вход тот же, что на экземпляре без модуля.
    const engineer = access(['work.facts.read', ...reads]);
    const withoutWork = access(reads);
    expect(enter(menu, all(engineer), engineer)).toBe(enter(menu, all(withoutWork), withoutWork));
    expect(enter(menu, all(engineer), engineer)).toBe('/home');
  });

  it('модуль выключен — пункты «Работ» в расчёт не попадают', () => {
    // Выключенного модуля нет в `/api/account/access` вовсе, а права его не объявлены.
    const off: AccessInfo = { permissions: reads, modules: [] };
    expect(enter(menu, all(off), off)).toBe('/home');
    const fitterOff: AccessInfo = { permissions: ['core.files.use'], modules: [] };
    expect(enter(menu, all(fitterOff), fitterOff)).toBeNull();
  });
});
