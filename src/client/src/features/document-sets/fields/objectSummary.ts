import { isFieldRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { formatFieldValue, type FieldTypeDefs } from '@/shared/utils/fieldDisplay';

/**
 * Сводка первых заполненных полей объекта — для свёрнутого/строкового вида составного (issue #102).
 *
 * Значение форматируем по типу поля (issue #611): без `defs` сводка показывала сырое хранимое
 * значение — дату ISO там, где поле внутри того же объекта показывает «ДД.ММ.ГГГГ».
 *
 * Живёт отдельным модулем (issue #663), а не в `ComplexFields.tsx`: функция чистая, и её зовёт
 * `extractToCommonData` — тянуть ради неё в тест всё дерево контролов (Radix, lucide, react-query)
 * значило бы проверять React там, где проверяется текст.
 */
export function objectSummary(
  values: Record<string, unknown>, fields: SchemaField[], defs: FieldTypeDefs = {},
): string {
  const parts = fields
    .map(f => {
      const v = values[f.key];
      if (v == null || v === '') return null;
      if (isFieldRef(v)) return v.displayName;
      if (typeof v === 'object') return null; // вложенные объекты/массивы — не в сводку
      return formatFieldValue(f, v, defs);
    })
    .filter((s): s is string => !!s)
    .slice(0, 3);
  return parts.length ? parts.join(' · ') : '(пусто)';
}
