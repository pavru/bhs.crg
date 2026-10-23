import type { AccessInfo } from '@/shared/api/access';
import { visibleNav } from './navAccess';
import type { NavItem } from './navConfig';
import { availableModules, inWorkspace, type Workspace } from './workspaces';

/**
 * Страница входа (ТЗ AUTH-16.1, AUTH-16.3, plan-02): куда вести после входа и по адресу `/`.
 *
 * <p>Раньше `/` вёл на «Стройки» всех — и роль без `core.constructions.read` (самодельная, с одними
 * правами модуля) после входа видела отказ. Теперь адрес вычисляется из того же ответа доступа, что
 * строит меню, и из открытого пространства: вход ведёт туда, куда человек может попасть и через меню.
 * Названий ролей правило не читает (AUTH-14).</p>
 *
 * <p>Порядок: видимый в пространстве пункт с наименьшим рангом (`NavItem.entry`), иначе первый
 * видимый, иначе никуда — тогда страница «нет доступа» (AUTH-15), а не раздел, отвечающий отказом.</p>
 */

/**
 * Ранг Главной («Продолжить работу», `shell-08-home`, решение владельца 22.10.2026, вариант Б).
 * Одна шкала для всех модулей: пункт модуля открывается вместо Главной, только если его ранг меньше.
 * Пока пункта Главной нет, константа в расчёт не входит.
 */
export const HOME_ENTRY_RANK = 100;

/** Пункт Главной — пункт ядра с рангом Главной. Его вписывает `shell-08`; механизм от этого не меняется. */
export function isHomeItem(item: NavItem): boolean {
  return !item.module && item.entry === HOME_ENTRY_RANK;
}

/**
 * Ранг ниже Главной действует, только если аккаунту открыт ровно один модуль — модуль пункта (правило
 * «одного модуля», П0). Иначе «Администратор», которому видно всё, попадал бы на страницу одного
 * модуля. Модули считаются по ДОСТУПУ АККАУНТА, а не пространства: вход не должен зависеть от
 * последнего выбранного пространства.
 */
function soleModuleOf(item: NavItem, access: AccessInfo): boolean {
  const open = availableModules(access);
  return !!item.module && open.length === 1 && open[0].code === item.module;
}

/**
 * Адрес входа: `null` — видимых пунктов нет, показывать страницу AUTH-15.
 *
 * @param items все пункты навигации по порядку меню (рабочие, затем «Система»): ничья при равных
 *   рангах решается этим порядком, то есть порядком рейла.
 */
export function entryPage(items: NavItem[], ws: Workspace, access: AccessInfo): string | null {
  const shown = visibleNav(items, access).filter(item => inWorkspace(item, ws, access));
  const homeDeclared = shown.some(isHomeItem);
  const ranked = shown.filter(item => item.entry !== undefined
    && (!homeDeclared || item.entry >= HOME_ENTRY_RANK || soleModuleOf(item, access)));
  // Сортировка устойчива: при равных рангах выигрывает пункт, стоящий в меню раньше.
  const best = [...ranked].sort((a, b) => a.entry! - b.entry!)[0];
  return (best ?? shown[0])?.to ?? null;
}

/**
 * Что в объявлении рангов нарушает правило: пункт ядра с рангом ниже Главной. Ядро видно каждому, и
 * такой ранг увёл бы от Главной всех — правило «одного модуля» к пункту ядра неприменимо (П0).
 */
export function entryRankProblems(items: NavItem[]): string[] {
  return items
    .filter(item => !item.module && item.entry !== undefined && item.entry < HOME_ENTRY_RANK)
    .map(item => `«${item.label}»: пункт ядра с рангом входа ${item.entry} ниже Главной (${HOME_ENTRY_RANK})`);
}
