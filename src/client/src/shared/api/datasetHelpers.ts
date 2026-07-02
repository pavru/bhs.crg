import type { FilterGroup, FilterNode, DataSetBindingPreviewResult } from './types';

/** A column descriptor cached on a DataSetSource. */
export interface DataSetColumn {
  name: string;
  sampleValues?: string[];
}

/** Safely parses the cached JSON schema of a DataSetSource into column descriptors. */
export function parseSourceColumns(cachedSchema: string | undefined | null): DataSetColumn[] {
  if (!cachedSchema) return [];
  try {
    const parsed = JSON.parse(cachedSchema);
    return Array.isArray(parsed) ? (parsed as DataSetColumn[]) : [];
  } catch {
    return [];
  }
}

/** Convenience: column names only. */
export function parseSourceColumnNames(cachedSchema: string | undefined | null): string[] {
  return parseSourceColumns(cachedSchema).map(c => c.name);
}

// ─── Reference (catalog) mapping ────────────────────────────────────────────
// Составное поле элемента может заполняться ссылкой на запись каталога: значение
// колонки ищется среди записей составного типа. Кодируется в значении маппинга
// строкой "@@ref:{json}". Формат разделяется с backend (DataSetMappingValue).

const REF_PREFIX = '@@ref:';

export interface RefMapping {
  /** Колонка файла со значением для поиска. */
  column: string;
  /** Поле записи каталога для сопоставления; пусто = по отображаемому имени. */
  match: string;
  /** ID составного типа каталога. */
  typeId: string;
}

export function isRefMappingValue(value: string | undefined | null): boolean {
  return typeof value === 'string' && value.startsWith(REF_PREFIX);
}

export function parseRefMapping(value: string | undefined | null): RefMapping | null {
  if (!isRefMappingValue(value)) return null;
  try {
    const parsed = JSON.parse((value as string).slice(REF_PREFIX.length)) as Partial<RefMapping>;
    if (!parsed.typeId || !parsed.column) return null;
    return { column: parsed.column, match: parsed.match ?? '', typeId: parsed.typeId };
  } catch {
    return null;
  }
}

export function buildRefMapping(m: RefMapping): string {
  return REF_PREFIX + JSON.stringify({ column: m.column, match: m.match, typeId: m.typeId });
}

// ─── Файловый маппинг ───────────────────────────────────────────────────────
// Поле типа "file" заполняется вложением, синтезированным из колонок ТОЙ ЖЕ строки источника
// (в отличие от ref-маппинга — здесь нет поиска по каталогу). Кодируется строкой
// "@@file:{json}". Формат разделяется с backend (DataSetMappingValue.ResolveFileValue).

const FILE_PREFIX = '@@file:';

export interface FileMapping {
  /** Колонка с путём к blob'у (напр. "ФайлПуть"). */
  column: string;
  /** Необязательная колонка с размером в байтах (напр. "РазмерБайт"); пусто — size=0. */
  sizeColumn: string;
}

export function isFileMappingValue(value: string | undefined | null): boolean {
  return typeof value === 'string' && value.startsWith(FILE_PREFIX);
}

export function parseFileMapping(value: string | undefined | null): FileMapping | null {
  if (!isFileMappingValue(value)) return null;
  try {
    const parsed = JSON.parse((value as string).slice(FILE_PREFIX.length)) as Partial<FileMapping>;
    if (!parsed.column) return null;
    return { column: parsed.column, sizeColumn: parsed.sizeColumn ?? '' };
  } catch {
    return null;
  }
}

export function buildFileMapping(m: FileMapping): string {
  return FILE_PREFIX + JSON.stringify({ column: m.column, sizeColumn: m.sizeColumn || undefined });
}

// ─── Слияние результата preview биндингов в значения формы ─────────────────────
// Клиентское зеркало серверного CommonDataBindingMerge (Application/Documents) — те же правила:
// пустое скалярное значение не затирает существующее, табличное поле пишется целиком (даже []).

export function mergeBindingPreviewsIntoValues(
  values: Record<string, unknown>,
  previews: DataSetBindingPreviewResult[],
): Record<string, unknown> {
  const next = { ...values };
  for (const p of previews) {
    if (p.error) continue;
    if (p.mode === 'scalar') {
      const data = p.data as Record<string, unknown>;
      for (const [key, value] of Object.entries(data)) {
        if (value === null || value === '') continue;
        next[key] = value;
      }
    } else if (p.mode === 'tabular' && p.targetFieldKey) {
      next[p.targetFieldKey] = p.data;
    }
  }
  return next;
}

/** Ключи полей, покрытых биндингами: скалярные (top-level) отдельно от табличных (array-полей). */
export function computeBoundFieldKeys(
  bindings: { targetFieldKey: string | null; mapping: Record<string, string> }[],
): { scalarKeys: Set<string>; arrayKeys: Set<string> } {
  const scalarKeys = new Set<string>();
  const arrayKeys = new Set<string>();
  for (const b of bindings) {
    if (b.targetFieldKey === null) {
      for (const key of Object.keys(b.mapping)) scalarKeys.add(key);
    } else {
      arrayKeys.add(b.targetFieldKey);
    }
  }
  return { scalarKeys, arrayKeys };
}

/** Recursively counts non-empty conditions in a filter tree. */
export function countFilterConditions(node: FilterNode | null | undefined): number {
  if (!node) return 0;
  if (node.type === 'condition') return node.column ? 1 : 0;
  return (node as FilterGroup).children.reduce((sum, c) => sum + countFilterConditions(c), 0);
}

/**
 * Prunes empty conditions (blank column) and empty groups from a filter tree.
 * Returns null when nothing meaningful remains.
 */
export function cleanFilterNode(node: FilterNode): FilterNode | null {
  if (node.type === 'condition') {
    return node.column.trim() ? node : null;
  }
  const validChildren = node.children
    .map(cleanFilterNode)
    .filter((c): c is FilterNode => c !== null);
  if (validChildren.length === 0) return null;
  return { ...node, children: validChildren };
}
