import type { CatalogScope } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';

export const STATUS_LABELS: Record<string, string> = {
  Draft: 'Черновик', Generating: 'Генерация...', Generated: 'Готово', Failed: 'Ошибка',
};
export const STATUS_COLORS: Record<string, string> = {
  Draft: 'bg-muted text-fg2',
  Generating: 'bg-warning-subtle text-warning',
  Generated: 'bg-success-subtle text-success',
  Failed: 'bg-danger-subtle text-danger',
};
export const SCOPE_COLORS: Record<CatalogScope, string> = {
  Set: 'bg-success-subtle text-success',
  Section: 'bg-brand-subtle text-brand-hover',
  Construction: 'bg-warning-subtle text-warning',
  System: 'bg-muted text-fg2',
};

export function fieldInputClass(invalid = false) {
  return `w-full border rounded-md px-3 py-2 text-sm text-fg1 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
    invalid ? 'border-danger focus-visible:ring-danger' : 'border-stroke-strong'
  }`;
}

export const TABLE_SHOWN_TYPES = new Set([
  'string', 'text', 'number', 'date', 'boolean', 'enum', 'primitive', 'complex',
]);

export const DEFAULT_COL_WIDTHS: Partial<Record<string, number>> = {
  number: 80, date: 118, boolean: 52, enum: 130, complex: 170,
};
export function defaultColWidth(f: SchemaField) {
  return DEFAULT_COL_WIDTHS[f.type] ?? 140;
}

export const CELL_INPUT =
  'w-full h-full px-1.5 bg-transparent border-none outline-none text-xs text-fg1 tabular-nums';

export function tryPrettyJson(val: unknown): string {
  try { return JSON.stringify(val, null, 2); } catch { return '{}'; }
}

export function tryParseJson(s: string): { ok: boolean; value?: Record<string, unknown>; error?: string } {
  try { return { ok: true, value: JSON.parse(s) as Record<string, unknown> }; }
  catch (e) { return { ok: false, error: String(e) }; }
}
