import { createContext, useContext } from 'react';

/**
 * Идентификаторы записей общих данных, на которые ссылаются данные формы: `{$ref:'catalog', entryId}`
 * на любой глубине — в поле, в строке таблицы, во вложенном составном значении. Зеркало серверного
 * `CatalogRefs.IdsIn`: вопрос «какие из моих ссылок — в архиве» задаётся про тот же набор.
 */
export function catalogRefIds(data: unknown): string[] {
  const found = new Set<string>();
  const walk = (v: unknown) => {
    if (Array.isArray(v)) { v.forEach(walk); return; }
    if (v == null || typeof v !== 'object') return;
    const o = v as Record<string, unknown>;
    if (o.$ref === 'catalog' && typeof o.entryId === 'string') found.add(o.entryId);
    Object.values(o).forEach(walk);
  };
  walk(data);
  // Порядок устойчивый: список едет в ключ запроса, и та же форма не должна спрашивать дважды.
  return [...found].sort();
}

const NONE: ReadonlySet<string> = new Set();

/**
 * Какие записи, на которые ссылается открытая форма, лежат в архиве (issue #1185).
 *
 * Контекстом, а не пропом: плитка ссылки сидит на дне рекурсии схемы (поле → составное → таблица →
 * строка → поле), и проп пришлось бы тащить через каждый её уровень. Вне поставщика набор пуст —
 * форма, которая о нём не спросила, пометок не рисует, но и не врёт.
 */
export const ArchivedRefsContext = createContext<ReadonlySet<string>>(NONE);

/** Указывает ли значение поля на запись в архиве. */
export function useIsArchivedRef(value: unknown): boolean {
  const archived = useContext(ArchivedRefsContext);
  if (value == null || typeof value !== 'object') return false;
  const ref = value as { $ref?: unknown; entryId?: unknown };
  return ref.$ref === 'catalog' && typeof ref.entryId === 'string' && archived.has(ref.entryId);
}
