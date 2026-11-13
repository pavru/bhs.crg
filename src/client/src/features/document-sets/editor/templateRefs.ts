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
 * Эффективный шаблон — как его выберет предпросмотр (`PreviewDocument`): ПЕРВЫЙ из выбранных, что
 * действует, → одиночный пин → по умолчанию → первый действующий. Раньше брался строго первый id
 * выбора: висячий первым — и эффективным тихо становился шаблон по умолчанию, а не отмеченный.
 */
export function effectiveTemplateOf(
  selectedIds: readonly string[], active: readonly Template[], pinnedId: string | null,
): Template | undefined {
  for (const id of selectedIds) {
    const t = active.find(x => x.id === id);
    if (t) return t;
  }
  return active.find(t => t.id === pinnedId)
    ?? active.find(t => t.isDefault)
    ?? active[0];
}
