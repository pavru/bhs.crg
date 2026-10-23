import { describe, it, expect } from 'vitest';
import { ClipboardList, House, Wrench } from 'lucide-react';
import type { AccessInfo } from '@/shared/api/access';
import { HOME_ENTRY_RANK, entryPage, entryRankProblems } from './entryPage';
import { settingsNav, workNav, type NavItem } from './navConfig';
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

// Пункты модулей — тестовые: настоящие вписывают plan-09 («Сметы») и work-03 («Мои отчёты»).
const estimates: NavItem = { to: '/estimates', label: 'Сметы', icon: ClipboardList, module: 'plan', permission: 'plan.estimate.read', rail: 'plan' };
const myReports: NavItem = { to: '/my-reports', label: 'Мои отчёты', icon: Wrench, module: 'work', permission: 'work.facts.write', rail: 'plan', entry: 10 };
// Пункт Главной вписывает shell-08: пункт ядра без права с рангом Главной.
const home: NavItem = { to: '/home', label: 'Главная', icon: House, rail: 'constructions', entry: HOME_ENTRY_RANK };

const nav = [...workNav, estimates, myReports, ...settingsNav];
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

  it('пункт входа вне пространства — страница входа этого пространства, без смены пространства', () => {
    const fitter = access(['core.constructions.read', 'work.facts.write', 'costs.invoice.read'], WORK, COSTS);
    const costsOnly = resolveWorkspace(fitter, [], 'module:costs').workspace;
    // Без Главной «Мои отчёты» с рангом открылись бы везде, где видны; в пространстве «Счета» их нет.
    expect(enter(nav, all(fitter), fitter)).toBe('/my-reports');
    expect(enter(nav, costsOnly, fitter)).toBe('/document-sets');
  });

  it('Главной нет — наименьший ранг, правило одного модуля не действует', () => {
    const admin = access(['core.constructions.read', 'plan.estimate.read', 'work.facts.write'], PLAN, WORK);
    expect(enter(nav, all(admin), admin)).toBe('/my-reports');
  });

  it('при равных рангах в одном разделе рейла — пункт, стоящий в меню раньше', () => {
    const a: NavItem = { ...estimates, entry: 50 };
    const b: NavItem = { ...myReports, entry: 50 };
    const both = access(['plan.estimate.read', 'work.facts.write'], PLAN, WORK);
    expect(enter([a, b], all(both), both)).toBe('/estimates');
    expect(enter([b, a], all(both), both)).toBe('/my-reports');
  });

  it('при равных рангах в разных разделах — порядок рейла, а не порядок массива', () => {
    // «Затраты» в рейле раньше «Планов»: массив меню по рейлу не упорядочен (`/periods` до `/reconciliations`).
    const plan: NavItem = { ...estimates, entry: 50 };
    const costs: NavItem = { ...myReports, to: '/costs-entry', rail: 'costs', entry: 50 };
    const both = access(['plan.estimate.read', 'work.facts.write'], PLAN, WORK);
    expect(enter([plan, costs], all(both), both)).toBe('/costs-entry');
  });
});

/** Главная в расчёте (решение владельца 22.10.2026, вариант Б `shell-08-home`). */
describe('страница входа и Главная', () => {
  it('ранг ниже Главной при одном доступном модуле — открывается пункт, а не Главная', () => {
    const fitter = access(['core.constructions.read', 'work.facts.write'], WORK);
    expect(enter(withHome, all(fitter), fitter)).toBe('/my-reports');
  });

  it('два доступных модуля — Главная, даже при ранге ниже её', () => {
    const manager = access(['core.constructions.read', 'work.facts.write', 'plan.estimate.read'], WORK, PLAN);
    expect(enter(withHome, all(manager), manager)).toBe('/home');
  });

  it('модули считаются по аккаунту, а не по пространству', () => {
    const manager = access(['core.constructions.read', 'work.facts.write', 'plan.estimate.read'], WORK, PLAN);
    const workOnly = resolveWorkspace(manager, [], 'module:work').workspace;
    expect(enter(withHome, workOnly, manager)).toBe('/home');
  });

  it('Главная, которой человеку не видно, всё равно объявлена: правило одного модуля действует', () => {
    const closedHome: NavItem = { ...home, permission: 'core.home.read' };
    const manager = access(['core.constructions.read', 'work.facts.write', 'plan.estimate.read'], WORK, PLAN);
    // Без Главной в видимом «Мои отчёты» с рангом ниже неё не открываются: модулей два.
    expect(enter([closedHome, ...nav], all(manager), manager)).toBe('/document-sets');
  });

  it('все права — Главная', () => {
    const admin = access(['core.constructions.read', 'work.facts.write', 'plan.estimate.read', 'costs.invoice.read'], WORK, PLAN, COSTS);
    expect(enter(withHome, all(admin), admin)).toBe('/home');
  });

  it('ранг больше ранга Главной — Главная', () => {
    const late: NavItem = { ...myReports, entry: HOME_ENTRY_RANK + 10 };
    const fitter = access(['work.facts.write'], WORK);
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
    const early: NavItem = { ...workNav[0], entry: 1 };
    expect(entryRankProblems([early, myReports])).toEqual([
      `«Стройки»: пункт ядра с рангом входа 1 ниже Главной (${HOME_ENTRY_RANK})`,
    ]);
  });
});
