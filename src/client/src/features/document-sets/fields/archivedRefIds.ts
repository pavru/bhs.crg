import { createContext, useContext } from 'react';

/**
 * Идентификаторы записей общих данных, на которые ссылаются данные формы: `{$ref:'catalog', entryId}`
 * на любой глубине — в поле, в строке таблицы, во вложенном составном значении. Зеркало серверного
 * `CatalogRefs.IdsIn`: вопрос «какие из моих ссылок — в архиве» задаётся про тот же набор.
 *
 * Вторая форма ссылки — «основа», `_baseRef`: `{kind, id}` или голая строка. Вид «не catalog»
 * (документ) пропускаем; голую строку берём — сервер спросит про записи общих данных, и
 * идентификатор документа среди них не найдётся.
 */
export function catalogRefIds(data: unknown): string[] {
  const found = new Set<string>();
  const walk = (v: unknown) => {
    if (Array.isArray(v)) { v.forEach(walk); return; }
    if (v == null || typeof v !== 'object') return;
    const o = v as Record<string, unknown>;
    if (o.$ref === 'catalog' && typeof o.entryId === 'string') found.add(o.entryId);
    for (const [key, child] of Object.entries(o)) {
      if (key !== '_baseRef') { walk(child); continue; }
      // Основа — указатель, а не данные: внутрь не идём.
      if (typeof child === 'string') found.add(child);
      else if (child && typeof child === 'object') {
        const base = child as { kind?: unknown; id?: unknown };
        if ((base.kind === undefined || base.kind === 'catalog') && typeof base.id === 'string') found.add(base.id);
      }
    }
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

/**
 * Объект, чью форму правят: документ или запись общих данных, если он уже сохранён. Нужен выносу
 * значения в общие данные — сервер считает ссылки, сохранённые в этом объекте, стоявшими, и вынос
 * значения со ссылкой на архивную запись не получает отказа (ревью PR #1230).
 */
export const RefsOwnerContext = createContext<string | undefined>(undefined);

/** Указывает ли значение поля на запись в архиве. */
export function useIsArchivedRef(value: unknown): boolean {
  const archived = useContext(ArchivedRefsContext);
  if (value == null || typeof value !== 'object') return false;
  const ref = value as { $ref?: unknown; entryId?: unknown };
  return ref.$ref === 'catalog' && typeof ref.entryId === 'string' && archived.has(ref.entryId);
}
