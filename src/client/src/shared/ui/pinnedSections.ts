import type { AccessInfo } from '@/shared/api/access';
import { allowed, missingFor } from './navAccess';
import type { NavItem } from './navConfig';
import { railDef } from './railSections';

/**
 * Закреплённые страницы навигации (ТЗ AUTH-16.4, work-04; макет Р0 — стол А).
 *
 * Список — серверная настройка `pinnedSections`: JSON-массив адресов `NavItem.to` в порядке, выбранном
 * человеком. Отдельного кода у страницы нет: второй реестр страниц ради закреплений разошёлся бы с
 * навигацией молча. Адрес, которого новая версия не знает, остаётся в списке и честно показан в
 * профиле «страницы больше нет» — снимает его только «Открепить».
 *
 * Здесь — только правила, без React: какие закрепления стоят в группе подпанели, как каждое выглядит
 * в профиле и как меняется сам список. Проверяются они тестом, а не отрисовкой оболочки.
 */

/** Ключ настройки на сервере. Имя — со времён «закреплённых разделов» ТЗ; в подписях это «страницы». */
export const PINNED_KEY = 'pinnedSections';
/** Свёрнута ли группа «Закреплённые» (`expanded | collapsed`, ключа нет — развёрнута). */
export const PINNED_GROUP_KEY = 'pinnedGroup';
/** Предел — как у сервера (`PinnedSectionValues.MaxPinned`). */
export const MAX_PINNED = 20;

export const PINNED_GROUP_LABEL = 'Закреплённые';
export const PIN_LABEL = 'Закрепить страницу';
export const UNPIN_LABEL = 'Открепить страницу';
export const LIMIT_HINT = `Закреплено ${MAX_PINNED} из ${MAX_PINNED} — открепите что-нибудь в профиле`;

/**
 * Список из значения настройки. Ключа нет — закреплений нет. Негодное значение (сервер его не примет,
 * но старое зеркало браузера могло остаться) — тоже «нет», а не падение оболочки.
 */
export function parsePinned(value: string | null | undefined): string[] {
  if (!value) return [];
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) ? [...new Set(parsed.filter((x): x is string => typeof x === 'string'))] : [];
  } catch {
    return [];
  }
}

/** Значение настройки из списка. Пустой список не хранится — ключ снимается (`null`), как у избранного. */
export function serializePinned(list: string[]): string | null {
  return list.length ? JSON.stringify(list) : null;
}

/** Закрепить: новое встаёт последним (решение владельца по Р0). Уже закреплённое и предел — без изменений. */
export function pin(list: string[], to: string): string[] {
  return list.includes(to) || list.length >= MAX_PINNED ? list : [...list, to];
}

export function unpin(list: string[], to: string): string[] {
  return list.filter(x => x !== to);
}

/** Переставить закрепление с места `from` на место `to` (перетаскивание, «Выше» / «Ниже»). */
export function move(list: string[], from: number, to: number): string[] {
  if (from === to || from < 0 || to < 0 || from >= list.length || to >= list.length) return list;
  const next = [...list];
  const [taken] = next.splice(from, 1);
  next.splice(to, 0, taken);
  return next;
}

/** Пункт группы «Закреплённые»: страница, её раздел второй строкой и пометка «вне пространства». */
export interface PinnedEntry {
  item: NavItem;
  section: string;
  outside: boolean;
}

/**
 * Пункты группы «Закреплённые» — в порядке списка.
 *
 * `visible` — пункты, уже отобранные по правам (`visibleNav`), из ВСЕХ разделов: закреплённая страница
 * вне пространства в группе есть, с пометкой (ТЗ AUTH-16.1 — открывается с плашкой, пространство не
 * меняется). Недоступной и неизвестной навигации страницы в группе нет: дверь в отказ меню не
 * рисует, а увидеть её можно в профиле. «Администрирование» пространство не сужает — его страница
 * пометки не получает.
 */
export function pinnedGroup(
  pinned: string[],
  visible: NavItem[],
  inside: (item: NavItem) => boolean,
): PinnedEntry[] {
  return pinned.flatMap(to => {
    const item = visible.find(n => n.to === to);
    if (!item) return [];
    const def = railDef(item.rail);
    return [{ item, section: def.title, outside: !def.admin && !inside(item) }];
  });
}

/** Вторая строка пункта группы: раздел страницы и, если надо, «вне пространства». */
export function entryNote(entry: PinnedEntry): string {
  return entry.outside ? `${entry.section} · вне пространства` : entry.section;
}

/**
 * Строка профиля «Закреплённые страницы» — шесть видов (макет Р0, лист «Общее», п. 6). Причина берётся
 * из ответа `/api/account/access`, а не догадкой по пустой странице:
 * - `module-off` — модуля нет в ответе: сервер отдаёт только включённые модули экземпляра, и такой
 *   отказ не исправит никто на экземпляре;
 * - `module-not-granted` — модуль есть, но не для вас: выдаёт администратор;
 * - `permission` — нет права (выдаваемого на этом экземпляре);
 * - `unknown` — `missingFor` причины не назвал: придумать правдоподобную хуже, чем признаться;
 * - `checking` — доступ ещё не пришёл: «ещё не знаем» — не отказ, и причину до ответа не называем.
 */
export type PinnedRow =
  | { kind: 'ok'; to: string; item: NavItem; section: string }
  | { kind: 'permission'; to: string; item: NavItem; codes: string[] }
  | { kind: 'module-not-granted'; to: string; item: NavItem; module: string }
  | { kind: 'module-off'; to: string; item: NavItem }
  | { kind: 'gone'; to: string }
  | { kind: 'unknown'; to: string; item: NavItem }
  | { kind: 'checking'; to: string; item: NavItem };

/**
 * Каждое закрепление — строкой: из списка молча не пропадает ничего. `nav` — вся навигация, без отбора;
 * `access` — ответ доступа, `undefined` пока он не пришёл.
 */
export function pinnedRows(pinned: string[], nav: NavItem[], access: AccessInfo | undefined): PinnedRow[] {
  return pinned.map<PinnedRow>(to => {
    const item = nav.find(n => n.to === to);
    if (!item) return { kind: 'gone', to };
    if (!access) return { kind: 'checking', to, item };
    if (allowed(item, access)) return { kind: 'ok', to, item, section: railDef(item.rail).title };
    const missing = missingFor(item, access);
    if (missing?.kind === 'module') {
      return access.modules.some(m => m.code === missing.code)
        ? { kind: 'module-not-granted', to, item, module: missing.title }
        : { kind: 'module-off', to, item };
    }
    if (missing?.kind === 'permission') return { kind: 'permission', to, item, codes: missing.codes };
    return { kind: 'unknown', to, item };
  });
}

/** Что сказать под названием строки профиля. */
export function rowNote(row: PinnedRow): string {
  switch (row.kind) {
    case 'ok': return row.section;
    case 'permission': return row.codes.length === 1
      ? `Нет доступа: нужно право ${row.codes[0]}`
      : `Нет доступа: нужно одно из прав ${row.codes.join(', ')}`;
    case 'module-not-granted': return `Модуль «${row.module}» вам не выдан — его выдаёт администратор`;
    case 'module-off': return 'Модуль страницы выключен на этом экземпляре';
    case 'gone': return 'Страницы больше нет в навигации';
    case 'unknown': return 'Нет доступа, причина неизвестна';
    case 'checking': return 'Проверяем доступ…';
  }
}

/** Название строки профиля: у пропавшей страницы — её адрес как есть (подпись не хранится, решение Р0). */
export function rowTitle(row: PinnedRow): string {
  return row.kind === 'gone' ? row.to : row.item.label;
}
