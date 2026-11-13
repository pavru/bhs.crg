import type { Template } from '@/shared/api/types';

/**
 * Ссылки документа на шаблоны — выбор для генерации и ключи параметров — через ИЗВЕСТНЫЕ шаблоны
 * типа (issue #1960).
 *
 * Документ мог хранить id шаблонов, которых давно нет: их показ не рисует, снять их человек не
 * может, а запись пересылала их обратно с каждым щелчком. Теперь сервер такую запись отвергает
 * (400) — значит, отсюда они уйти не должны, а в выборе «первого» — участвовать.
 */
export function knownTemplateIds(ids: readonly string[], templates: readonly Pick<Template, 'id'>[]): string[] {
  const known = new Set(templates.map(t => t.id));
  return ids.filter(id => known.has(id));
}

/** Переопределения параметров только известных шаблонов. */
export function knownTemplateParams<T>(
  overrides: Record<string, T>, templates: readonly Pick<Template, 'id'>[],
): Record<string, T> {
  const known = new Set(templates.map(t => t.id));
  return Object.fromEntries(Object.entries(overrides).filter(([id]) => known.has(id)));
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
