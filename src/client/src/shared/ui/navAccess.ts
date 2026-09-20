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
 * виден всем: таких немного и каждый — решение (свой профиль, свои уведомления).
 */
export function visibleNav(items: NavItem[], access: AccessInfo): NavItem[] {
  return items.filter(item => allowed(item, access));
}

export function allowed(item: NavItem, access: AccessInfo): boolean {
  if (item.module && !hasModule(access, item.module)) return false;
  if (item.permission && !hasPermission(access, item.permission)) return false;
  return true;
}

/**
 * Чего не хватает для пункта — чтобы страница отказа назвала причину, а не отказала молча
 * (ТЗ AUTH-15). Два случая разные: модуля нет у экземпляра вовсе (его не выдаст и администратор) и
 * права нет у пользователя (выдаётся).
 */
export function missingFor(item: NavItem, access: AccessInfo):
  { kind: 'module'; code: string; title: string } | { kind: 'permission'; code: string } | null {
  if (item.module && !hasModule(access, item.module)) {
    const known = access.modules.find(m => m.code === item.module);
    return { kind: 'module', code: item.module, title: known?.title ?? item.module };
  }
  if (item.permission && !hasPermission(access, item.permission)) {
    return { kind: 'permission', code: item.permission };
  }
  return null;
}
