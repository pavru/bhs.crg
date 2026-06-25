import type { FilterGroup, FilterNode } from './types';

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
