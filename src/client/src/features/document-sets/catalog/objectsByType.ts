import type { CommonDataEntry, DocumentType } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/**
 * Комплексный текстовый матч записи каталога (issue #249). Ищем подстроку `query` (регистронезависимо)
 * в: имени записи, её алиасах, имени её типа (искали «орга» → находим тип «Организация») и значениях
 * собственных скалярных полей. Служебные ключи (`_baseRef` и пр., префикс `_`) и составные значения
 * (объекты/массивы) пропускаем — как в превью строки. Пустой запрос матчит всё.
 */
export function entryMatchesQuery(entry: CommonDataEntry, typeName: string | undefined, query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  if (entry.displayName.toLowerCase().includes(q)) return true;
  if (entry.aliases?.some(a => a.toLowerCase().includes(q))) return true;
  if (typeName && typeName.toLowerCase().includes(q)) return true;
  return Object.entries(entry.data).some(([k, v]) =>
    !k.startsWith('_') && v != null && typeof v !== 'object' && String(v).toLowerCase().includes(q));
}

/** Группировка записей по составному типу (+ «без типа»). Порядок внутри группы — как во входе. */
export function groupObjectsByType(entries: CommonDataEntry[], types: DocumentType[]) {
  const groups = types
    .map(type => ({ type, items: entries.filter(e => e.compositeTypeId === type.id) }))
    .filter(g => g.items.length > 0);
  const noType = entries.filter(e => !types.some(t => t.id === e.compositeTypeId));
  return { groups, noType };
}
