import type { Template } from '@/shared/api/types';

/**
 * Ссылки документа на шаблоны — выбор для генерации и ключи параметров (issue #1960).
 *
 * Есть ли такой шаблон, решает СЕРВЕР: запись со ссылкой не на шаблон типа он отвергает (400), а
 * накопленные висячие убрала миграция. Здесь id, которого нет в загруженном списке, означает прежде
 * всего, что УСТАРЕЛ список: новая версия шаблона пересаживает выбор на свой id, а запрос списка
 * шаблонов — отдельный и от этого не обновляется. Выбросить такой id из записи значило бы молча
 * стереть выбор человека, поэтому его пересылают как есть, а список перечитывают.
 */
export function unknownTemplateRefs(
  ids: readonly string[], paramKeys: readonly string[], templates: readonly Pick<Template, 'id'>[],
): string[] {
  const known = new Set(templates.map(t => t.id));
  return [...new Set([...ids, ...paramKeys])].filter(id => !known.has(id)).sort();
}

/** Ключи переопределений параметров (id шаблонов); битый или пустой JSON — без ключей. */
export function paramTemplateIds(json: string | null | undefined): string[] {
  if (!json) return [];
  try {
    const o: unknown = JSON.parse(json);
    return o && typeof o === 'object' && !Array.isArray(o) ? Object.keys(o) : [];
  } catch { return []; }
}

/**
 * Эффективный шаблон — тем же правилом, что сервер (`TemplateChoice`, issue #1401):
 * - выбор есть — ПЕРВЫЙ из выбранных, что действует; ни одного — `undefined`: сервер выпускать
 *   откажет, и показать вместо этого запасной шаблон значило бы обещать документ, которого не будет
 *   (незнакомый id — прежде всего устаревший список, его перечитывают);
 * - без выбора — одиночный пин, в том числе на историческую версию (сервер её и берёт), → по
 *   умолчанию → первый действующий.
 *
 * `templates` — все версии типа в порядке сервера: шаблоны по созданию, внутри — от новой версии,
 * поэтому первый действующий — тот, что сервер возьмёт без «по умолчанию».
 */
export function effectiveTemplateOf(
  selectedIds: readonly string[], templates: readonly Template[], pinnedId: string | null,
): Template | undefined {
  const active = templates.filter(t => t.isActive);
  if (selectedIds.length > 0) {
    for (const id of selectedIds) {
      const t = active.find(x => x.id === id);
      if (t) return t;
    }
    return undefined;
  }
  return templates.find(t => t.id === pinnedId)
    ?? active.find(t => t.isDefault)
    ?? active[0];
}
