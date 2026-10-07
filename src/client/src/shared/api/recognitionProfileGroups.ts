/**
 * Профили распознавания по модулям (issue #1075): группы рейла, строка «скрыто столько-то» и подпись
 * привязки к профилю выключенного модуля.
 *
 * Отдельно от хуков — это чистая логика, и проверяется она тестом без сервера.
 */

/** То, по чему складывается группа: владелец приходит с сервера и у профиля, и у вида. */
interface Owned {
  module?: string | null;
  moduleTitle?: string | null;
}

export interface ModuleGroup<T> {
  /** Код владельца; пусто — владелец серверу неизвестен. */
  module: string;
  title: string;
  items: T[];
}

/** Профиль выключенного модуля — имя и владелец, без содержимого. */
export interface HiddenRecognitionProfile {
  id: string;
  name: string;
  kind: string;
  kindLabel: string;
  module: string | null;
  moduleTitle: string | null;
}

/** Вариант выбора профиля в селекте привязки. */
export interface ProfileOption {
  id: string;
  name: string;
  /** Показать можно, выбрать нельзя: профиль привязан, но на этом экземпляре им не читают. */
  disabled?: boolean;
}

const NO_OWNER = 'Без модуля';

/**
 * Складывает в группы по владельцу. Группы — по алфавиту названий, внутри порядок сохраняется: его
 * задал сервер (заводские выше своих).
 */
export function groupByModule<T extends Owned>(items: readonly T[]): ModuleGroup<T>[] {
  const groups = new Map<string, ModuleGroup<T>>();
  for (const item of items) {
    const module = item.module ?? '';
    let group = groups.get(module);
    if (!group) {
      group = { module, title: item.moduleTitle || NO_OWNER, items: [] };
      groups.set(module, group);
    }
    group.items.push(item);
  }
  return [...groups.values()].sort((a, b) => a.title.localeCompare(b.title, 'ru'));
}

/**
 * «Скрыто 4 профиля: модуль «…» выключен». null — скрывать нечего, и строки нет вовсе: «скрыто 0»
 * читалось бы как сообщение о чём-то.
 */
export function hiddenSummary(hidden: readonly HiddenRecognitionProfile[]): string | null {
  if (hidden.length === 0) return null;
  const titles = [...new Set(hidden.map(h => h.moduleTitle || NO_OWNER))].sort((a, b) => a.localeCompare(b, 'ru'));
  const n = hidden.length;
  const count = `${plural(n, 'Скрыт', 'Скрыто', 'Скрыто')} ${n} ${plural(n, 'профиль', 'профиля', 'профилей')}`;
  const reason = titles.length === 1
    ? `модуль «${titles[0]}» выключен`
    : `модули ${titles.map(t => `«${t}»`).join(', ')} выключены`;
  return `${count}: ${reason}`;
}

/** Подпись привязки к профилю выключенного модуля. */
export function hiddenLabel(h: HiddenRecognitionProfile): string {
  return `${h.name} — модуль «${h.moduleTitle || NO_OWNER}» выключен`;
}

/**
 * Варианты селекта привязки с учётом уже стоящей привязки.
 *
 * Привязан профиль, которого среди предлагаемых нет, — добавляем его строкой, которую нельзя выбрать.
 * Без неё селект показал бы «не выбрано» при стоящей привязке: идентификатор есть, а назвать его нечем.
 * Известен скрытым — называем причину; неизвестен вовсе (списки ещё грузятся) — не добавляем ничего:
 * «ещё не знаем» не повод рисовать «недоступен».
 */
export function withBoundOption(
  offered: readonly ProfileOption[],
  boundId: string | null | undefined,
  hidden: readonly HiddenRecognitionProfile[],
): ProfileOption[] {
  if (!boundId || offered.some(o => o.id === boundId)) return [...offered];
  const h = hidden.find(x => x.id === boundId);
  return h ? [...offered, { id: h.id, name: hiddenLabel(h), disabled: true }] : [...offered];
}

export function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}
