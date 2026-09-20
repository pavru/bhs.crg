import { describe, it, expect } from 'vitest';
import { visibleNav, allowed, missingFor } from './navAccess';
import { workNav, settingsNav } from './navConfig';
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
  it('администратор без права на типы не видит раздела типов', () => {
    // Все права администратора, КРОМЕ права на типы. Имени роли здесь нет — и в этом суть.
    const all = settingsNav.map(i => i.permission!).filter(Boolean);
    const withoutTypes = all.filter(p => p !== 'core.types.edit');

    const visible = visibleNav(settingsNav, access(withoutTypes));

    expect(visible.map(i => i.label)).not.toContain('Типы документов');
    expect(visible.map(i => i.label)).toContain('Пользователи');
  });

  it('право на типы открывает все три раздела типов разом', () => {
    const visible = visibleNav(settingsNav, access(['core.types.edit']));

    expect(visible.map(i => i.label))
      .toEqual(['Типы документов', 'Составные типы', 'Типы полей']);
  });

  it('пустой доступ не показывает ни одного пункта', () => {
    expect(visibleNav(workNav, access([]))).toHaveLength(0);
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

    expect(missingFor(users, access([]))).toEqual({ kind: 'permission', code: 'core.users.manage' });
    expect(missingFor(quality, access([], [{ code: 'id', available: false }])))
      .toEqual({ kind: 'module', code: 'id', title: 'id' });
  });

  it('когда всё на месте, причины отказа нет', () => {
    const users = settingsNav.find(i => i.to === '/users')!;
    expect(missingFor(users, access(['core.users.manage']))).toBeNull();
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
    const nameless = settingsNav.filter(i => !i.permission && !i.module);
    expect(nameless.map(i => i.label)).toEqual([]);
  });

  it('в рабочих разделах — у всех есть право или модуль', () => {
    const nameless = workNav.filter(i => !i.permission && !i.module);
    expect(nameless.map(i => i.label)).toEqual([]);
  });
});
