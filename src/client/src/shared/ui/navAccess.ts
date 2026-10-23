import type { AccessInfo } from '@/shared/api/access';
import { hasModule, hasPermission } from '@/shared/api/access';
import type { NavItem } from './navConfig';

/**
 * Отбор пунктов навигации по правам (ТЗ AUTH-16).
 *
 * Вынесено из компонента отдельной функцией НАРОЧНО: это правило, которое обязан проверять тест, а
 * проверять его через отрисовку сайдбара значило бы проверять заодно вёрстку, роутер и провайдеры
 * — и падать от любого из них.
 *
 * Пункт показывается, когда выполнены ВСЕ названные им условия. Пункт, не назвавший ни одного,
 * виден всем: таких немного и каждый — решение (свой профиль, свои уведомления). Список прав —
 * одно условие «любое из» (plan-02): пункт виден при любом праве списка, как и адрес под политикой
 * `AppPolicies.AnyPermission` на сервере.
 *
 * ⚠️ Пустой список — ЗАКРЫТ всем, а не «права не названо»: сервер такие ворота не строит вовсе, и
 * список, собранный из вкладов модулей, на экземпляре без вкладчиков открыл бы пункт любому.
 */
export function visibleNav(items: NavItem[], access: AccessInfo): NavItem[] {
  return items.filter(item => allowed(item, access));
}

export function allowed(item: NavItem, access: AccessInfo): boolean {
  if (item.module && !hasModule(access, item.module)) return false;
  if (item.permission !== undefined && !grantsAny(item, access)) return false;
  return true;
}

function grantsAny(item: NavItem, access: AccessInfo): boolean {
  return permissionsOf(item).some(code => hasPermission(access, code));
}

/**
 * Права пункта списком, без повторов (как `AppPolicies.AnyPermission` сервера): одно право — список
 * из одного, без права — пустой.
 */
export function permissionsOf(item: Pick<NavItem, 'permission'>): string[] {
  if (item.permission === undefined) return [];
  return [...new Set(typeof item.permission === 'string' ? [item.permission] : item.permission)];
}

/** Чего не хватает для раздела — причина на странице «нет доступа» (ТЗ AUTH-15). */
export type MissingAccess =
  | { kind: 'module'; code: string; title: string }
  | { kind: 'permission'; codes: string[] }
  | null;

/** Права ядра — `core.*` (договор кодов AUTH-1): они есть на любом экземпляре. */
const CORE = 'core';

/**
 * Чего не хватает для пункта — чтобы страница отказа назвала причину, а не отказала молча
 * (ТЗ AUTH-15). Два случая разные: модуля нет у экземпляра вовсе (его не выдаст и администратор) и
 * права нет у пользователя (выдаётся).
 *
 * ⚠️ Из списка «любого из» выпадают права модулей, которых нет на экземпляре (нет в
 * `access.modules`; модуль права — начало его кода, как в `ModuleAccess.IsOpen` сервера): такое
 * право не выдать никому, и страница предлагала бы просить у администратора несуществующее. Если
 * выпали все — причина неизвестна, и страница говорит это, а не называет пустой список.
 */
export function missingFor(item: NavItem, access: AccessInfo): MissingAccess {
  if (item.module && !hasModule(access, item.module)) {
    const known = access.modules.find(m => m.code === item.module);
    return { kind: 'module', code: item.module, title: known?.title ?? item.module };
  }
  if (item.permission !== undefined && !grantsAny(item, access)) {
    const issuable = permissionsOf(item).filter(code => onInstance(code, access));
    return issuable.length ? { kind: 'permission', codes: issuable } : null;
  }
  return null;
}

function onInstance(code: string, access: AccessInfo): boolean {
  const owner = code.split('.')[0];
  return owner === CORE || access.modules.some(m => m.code === owner);
}
