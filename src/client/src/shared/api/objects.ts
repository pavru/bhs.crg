import { apiClient } from './client';
import type { CatalogScope } from './types';

/** Стратегия сопоставления строки с существующим объектом каталога (зеркало backend). */
export type ObjectMatchStrategy = 'Field' | 'Name' | 'IdentityKey';

export interface ObjectResolveItem {
  typeId: string;
  strategy: ObjectMatchStrategy;
  /** Field: значение колонки; Name: искомое имя/алиас. */
  value?: string;
  /** Field: ключ под-поля, по которому матчим. */
  fieldKey?: string;
  /** IdentityKey: значения полей строки (fieldKey→value). */
  fields?: Record<string, string | null>;
}

export interface ObjectResolveResult {
  entryId: string;
  displayName: string | null;
  scope: CatalogScope;
  /**
   * Совпавшая запись в архиве (issue #1185): подставлять её молча нельзя. Действующая запись с тем
   * же ключом, если она есть, приходит вместо архивной — с признаком приходит только совпадение,
   * у которого действующей пары нет.
   */
  archived: boolean;
}

/**
 * Батч-резолв «строка→объект» (issue #183): находит СУЩЕСТВУЮЩИЕ объекты каталога в scope-поддереве.
 * Read-only — ничего не создаёт. Порядок результата = порядку items; элемент null — совпадения нет.
 */
export async function resolveObjectsBatch(
  scope: CatalogScope | undefined,
  scopeId: string | null | undefined,
  items: ObjectResolveItem[],
): Promise<(ObjectResolveResult | null)[]> {
  if (items.length === 0) return [];
  const { data } = await apiClient.post<(ObjectResolveResult | null)[]>('/objects/resolve-batch', {
    scope: scope ?? 'System',
    scopeId: scopeId ?? null,
    items,
  });
  return data;
}
