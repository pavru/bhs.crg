import { isHomeItem } from './entryPage';
import { describe, it, expect } from 'vitest';
import { visibleNav, allowed, missingFor, permissionsOf } from './navAccess';
import { Assignment } from './icons';
import { workNav, settingsNav, type NavItem } from './navConfig';
import type { AccessInfo } from '@/shared/api/access';

function access(permissions: string[], modules: { code: string; available: boolean }[] = []): AccessInfo {
  return { permissions, modules: modules.map(m => ({ ...m, title: m.code })) };
}

/**
 * Навигация строится по правам, а не по роли (issue #952, ТЗ AUTH-14/AUTH-16).
 *
 * ⚠️ Главный тест здесь — «администратор без права на типы не видит раздела типов». Он написан так,
 * чтобы его нельзя было пройти, читая роль: роли в наборе данных нет вовсе. Прежний код решал по
 * роли из токена, и такой пользователь видел раздел, который отвечает отказом.
 */
describe('навигация по правам', () => {
  it('Мои отчёты видны с правом своих отчётов при открытом модуле work', () => {
    const own = workNav.find(item => item.to === '/my-reports')!;
    const work = { code: 'work', available: true };
    expect(allowed(own, access(['work.report.own'], [work]))).toBe(true);
    expect(allowed(own, access(['work.report.review', 'work.report.read'], [work]))).toBe(false);
    expect(allowed(own, access(['work.report.own'], [{ ...work, available: false }]))).toBe(false);
  });
  it('Приёмка видна с правом приёмки, чтения отчётов или утверждения бригад при открытом модуле work', () => {
    const review = workNav.find(item => item.to === '/work-review')!;
    const work = { code: 'work', available: true };
    expect(allowed(review, access(['work.report.review'], [work]))).toBe(true);
    expect(allowed(review, access(['work.report.read'], [work]))).toBe(true);
    expect(allowed(review, access(['work.crew.approve'], [work]))).toBe(true);
    expect(allowed(review, access(['work.report.own'], [work]))).toBe(false);
    expect(allowed(review, access(['work.report.read'], [{ ...work, available: false }]))).toBe(false);
  });
  it('Сводка открыта менеджеру и руководителю при включённом учёте работ', () => {
    const summary = workNav.find(item => item.to === '/work-summary')!;
    const work = { code: 'work', available: true };
    expect(allowed(summary, access(['work.report.review'], [work]))).toBe(true);
    expect(allowed(summary, access(['work.report.read'], [work]))).toBe(true);
    expect(allowed(summary, access(['work.report.own', 'work.crew.approve'], [work]))).toBe(false);
    expect(allowed(summary, access(['work.report.read'], [{ ...work, available: false }]))).toBe(false);
  });

  it('администратор без права на типы не видит раздела типов', () => {
    // Все права администратора, КРОМЕ права на типы. Имени роли здесь нет — и в этом суть.
    const all = settingsNav.flatMap(i => i.permission ?? []);
    const withoutTypes = all.filter(p => p !== 'core.types.edit');

    const visible = visibleNav(settingsNav, access(withoutTypes));

    expect(visible.map(i => i.label)).not.toContain('Типы документов');
    expect(visible.map(i => i.label)).toContain('Пользователи');
  });

  it('право на типы открывает разделы типов и сопоставление полей разом', () => {
    const visible = visibleNav(settingsNav, access(['core.types.edit']));

    expect(visible.map(i => i.label))
      .toEqual(['Типы документов', 'Составные типы', 'Типы полей', 'Сопоставление полей']);
  });

  // «Сотрудники» (work-01): своё право чтения, право на общие данные пункт не открывает.
  it('пункт «Сотрудники» скрыт без core.employees.read', () => {
    expect(visibleNav(workNav, access(['core.catalog.read'])).map(i => i.label)).not.toContain('Сотрудники');
    const shown = visibleNav(workNav, access(['core.employees.read'])).filter(i => !isHomeItem(i));
    expect(shown.map(i => i.label)).toEqual(['Сотрудники']);
    expect(shown[0]).toMatchObject({ to: '/employees', section: 'employees', rail: 'data' });
  });

  it('пустой доступ не показывает ни одного пункта, кроме Главной', () => {
    // Главная открыта каждому вошедшему (shell-08, [UI-33]); работой она не считается (`hasWork`).
    expect(visibleNav(workNav, access([])).map(i => i.to)).toEqual(['/home']);
    expect(visibleNav(settingsNav, access([]))).toHaveLength(0);
  });

  it('пункт модуля закрыт, пока модуль недоступен', () => {
    const quality = workNav.find(i => i.to === '/quality-docs')!;

    expect(allowed(quality, access([], [{ code: 'id', available: false }]))).toBe(false);
    expect(allowed(quality, access([], [{ code: 'id', available: true }]))).toBe(true);
  });

  it('отказ называет, чего не хватает — права или модуля', () => {
    const users = settingsNav.find(i => i.to === '/users')!;
    const quality = workNav.find(i => i.to === '/quality-docs')!;

    expect(missingFor(users, access([]))).toEqual({ kind: 'permission', codes: ['core.users.manage'] });
    expect(missingFor(quality, access([], [{ code: 'id', available: false }])))
      .toEqual({ kind: 'module', code: 'id', title: 'id' });
  });

  it('когда всё на месте, причины отказа нет', () => {
    const users = settingsNav.find(i => i.to === '/users')!;
    expect(missingFor(users, access(['core.users.manage']))).toBeNull();
  });
});

/**
 * «Любое из прав» (plan-02, ТЗ AUTH-16): пункт под списком виден носителю любого права списка — как
 * «Ход работ», который видят носители прав любой из своих колонок. Пункт тестовый: настоящий вписывает
 * `plan-25`.
 */
describe('пункт под списком прав', () => {
  const progress: NavItem = {
    to: '/progress', label: 'Ход работ', icon: Assignment, rail: 'plan',
    permission: ['plan.estimate.read', 'costs.report.read'],
  };
  const both = [{ code: 'plan', available: false }, { code: 'costs', available: false }];

  it('виден при любом праве списка и скрыт без обоих', () => {
    expect(allowed(progress, access(['plan.estimate.read'], both))).toBe(true);
    expect(allowed(progress, access(['costs.report.read'], both))).toBe(true);
    expect(allowed(progress, access(['core.catalog.read'], both))).toBe(false);
    expect(visibleNav([progress], access(['costs.report.read'], both)).map(i => i.label)).toEqual(['Ход работ']);
  });

  it('отказ называет весь список', () => {
    expect(missingFor(progress, access([], both)))
      .toEqual({ kind: 'permission', codes: ['plan.estimate.read', 'costs.report.read'] });
  });

  it('право модуля, которого нет на экземпляре, отказ не называет', () => {
    // На экземпляре только «Счета и накладные»: право `plan.*` не выдать никому.
    expect(missingFor(progress, access([], [{ code: 'costs', available: false }])))
      .toEqual({ kind: 'permission', codes: ['costs.report.read'] });
  });

  it('права ядра называются всегда — ядро есть на любом экземпляре', () => {
    const item: NavItem = { ...progress, permission: ['core.constructions.read', 'plan.estimate.read'] };
    expect(missingFor(item, access([]))).toEqual({ kind: 'permission', codes: ['core.constructions.read'] });
  });

  it('выпали все права — причина неизвестна, а не пустой список', () => {
    expect(missingFor(progress, access([]))).toBeNull();
  });

  // Список «Хода работ» соберут из вкладов модулей (plan-25): без вкладчиков он пуст, и пустой
  // список, прочитанный как «права не названо», открыл бы пункт любому вошедшему.
  it('пустой список закрывает пункт, а не открывает его всем', () => {
    const empty: NavItem = { ...progress, permission: [] };
    expect(allowed(empty, access(['core.catalog.read'], both))).toBe(false);
    expect(missingFor(empty, access([], both))).toBeNull();
  });

  it('повтор кода в списке называется один раз', () => {
    const twice: NavItem = { ...progress, permission: ['costs.report.read', 'costs.report.read'] };
    expect(missingFor(twice, access([], both))).toEqual({ kind: 'permission', codes: ['costs.report.read'] });
  });
});

/**
 * Пункт меню обязан называть право ТОЙ ЖЕ строкой, что стоит на адресах его экрана. Здесь это
 * проверяется единственным доступным клиенту способом — что строка вообще названа: расхождение с
 * сервером ловит инвентаризация адресов на бэкенде.
 *
 * Без этого теста пункт без права виден всем, и заметить это можно только глазами администратора,
 * у которого всё равно есть всё.
 */
describe('каждый пункт назвал, чем открывается', () => {
  it('в разделах настройки — у всех есть право', () => {
    const nameless = settingsNav.filter(i => !permissionsOf(i).length && !i.module);
    expect(nameless.map(i => i.label)).toEqual([]);
  });

  it('в рабочих разделах — у всех есть право или модуль', () => {
    // Названное исключение — Главная (shell-08): открыта каждому вошедшему, её адрес под одним входом.
    const nameless = workNav.filter(i => !permissionsOf(i).length && !i.module && !isHomeItem(i));
    expect(nameless.map(i => i.label)).toEqual([]);
  });
});
