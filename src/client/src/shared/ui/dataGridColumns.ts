import type { DataGridColumn } from './DataGrid';

/**
 * Колонки сетки по СОХРАНЁННЫМ строкам и схеме их типа (G1a, issue #1088) — для экранов, где сервер
 * колонок не присылает, а строки уже лежат в записи (привязанный массив записи каталога).
 *
 * Состав — ключи ВСЕХ строк, а не первой: у union строка несёт поля одного варианта, и состав по
 * первой терял колонки остальных. Порядок и подписи — из схемы; ключ, которого в схеме нет, остаётся
 * колонкой с причиной `removed`. Схемы нет (тип не найден) — ключи как есть, без пометок: «не знаем»
 * не равно «поля нет».
 */
export function gridColumnsOf(
  rows: Record<string, unknown>[],
  fields: { key: string; title?: string }[] | null,
): DataGridColumn[] {
  const keys: string[] = [];
  const seen = new Set<string>();
  for (const row of rows)
    for (const key of Object.keys(row))
      if (!seen.has(key)) { seen.add(key); keys.push(key); }

  if (!fields) return keys.map(key => ({ key, label: key }));

  const known = new Set(fields.map(f => f.key));
  return [
    ...fields.filter(f => seen.has(f.key)).map(f => ({ key: f.key, label: f.title?.trim() || f.key })),
    ...keys.filter(k => !known.has(k)).map(k => ({ key: k, label: k, unavailable: 'removed' as const })),
  ];
}
