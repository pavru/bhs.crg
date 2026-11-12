import type { AccessInfo } from '@/shared/api/access';
import { HOME_ENTRY_RANK, railSections, type NavItem } from './navConfig';
import { availableModules } from './workspaces';

/**
 * Страница входа (ТЗ AUTH-16.1, AUTH-16.3, plan-02): куда вести после входа и по адресу `/`.
 *
 * <p>Раньше `/` вёл на «Стройки» всех — и роль без `core.constructions.read` (самодельная, с одними
 * правами модуля) после входа видела отказ. Теперь адрес вычисляется из того, что показывает меню
 * (`useNavSections`: доступ и открытое пространство): вход ведёт туда, куда человек может попасть и
 * через меню. Названий ролей правило не читает (AUTH-14).</p>
 *
 * <p>Порядок: показанный пункт с наименьшим рангом (`NavItem.entry`), иначе первый показанный, иначе
 * никуда — тогда страница «нет доступа» (AUTH-15), а не раздел, отвечающий отказом. Главная работой не
 * считается (`hasWork`): кроме неё ничего не показано — тоже никуда.</p>
 */

export { HOME_ENTRY_RANK };

/** Адрес страницы входа: пункт меню с этим адресом не откроется — `/` отвечает перенаправлением. */
const ENTRY_ADDRESS = '/';

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

const railIndex = (item: NavItem) => railSections.findIndex(r => r.id === item.rail);

/**
 * Есть ли человеку работа: показан хоть один пункт, кроме Главной (shell-08, ТЗ AUTH-15). Главная
 * открыта каждому, и без этого правила вход никогда не отвечал бы «никуда» — страница «доступ не
 * выдан» пропала бы у того, кому не открыто ничего. Тем же правилом Главная решает, говорить ли ей
 * первым блоком текст этой страницы: одно правило на двоих.
 */
export function hasWork(shown: NavItem[]): boolean {
  return shown.some(item => !isHomeItem(item));
}

/**
 * Адрес входа: `null` — показанных пунктов нет, показывать страницу AUTH-15.
 *
 * @param shown пункты, которые меню показывает этому человеку в этом пространстве, по порядку меню
 *   (`useNavSections`: рабочие, затем «Система») — отбор не повторяется здесь вторым экземпляром.
 * @param declared все объявленные пункты: Главная «объявлена», даже если этому человеку не видна, — и
 *   правило «одного модуля» от её видимости не зависит.
 */
export function entryPage(shown: NavItem[], access: AccessInfo, declared: NavItem[] = shown): string | null {
  if (!hasWork(shown)) return null;
  const homeDeclared = declared.some(isHomeItem);
  const ranked = shown.filter(item => item.entry !== undefined
    && (!homeDeclared || item.entry >= HOME_ENTRY_RANK || soleModuleOf(item, access)));
  // При равных рангах — порядок рейла (design plan-02); внутри раздела сортировка устойчива и
  // оставляет порядок меню.
  const best = [...ranked].sort((a, b) => a.entry! - b.entry! || railIndex(a) - railIndex(b))[0];
  return (best ?? shown[0])?.to ?? null;
}

/**
 * Что в объявлении рангов нарушает правила. Пункт ядра с рангом ниже Главной: ядро видно каждому, и
 * такой ранг увёл бы от Главной всех — правило «одного модуля» к пункту ядра неприменимо (П0). Два
 * пункта ядра с рангом Главной — две «Главные», и вход между ними решал бы порядок массива. Пункт по
 * адресу `/`: этот адрес — страница входа, он отвечает перенаправлением, и пункт там не откроется.
 */
export function entryRankProblems(items: NavItem[]): string[] {
  const problems = items
    .filter(item => !item.module && item.entry !== undefined && item.entry < HOME_ENTRY_RANK)
    .map(item => `«${item.label}»: пункт ядра с рангом входа ${item.entry} ниже Главной (${HOME_ENTRY_RANK})`);
  const homes = items.filter(isHomeItem);
  if (homes.length > 1)
    problems.push(`ранг Главной (${HOME_ENTRY_RANK}) у нескольких пунктов ядра: ${homes.map(i => `«${i.label}»`).join(', ')}`);
  for (const item of items.filter(i => i.to === ENTRY_ADDRESS))
    problems.push(`«${item.label}»: адрес «${ENTRY_ADDRESS}» — страница входа, пункт там не откроется`);
  return problems;
}
