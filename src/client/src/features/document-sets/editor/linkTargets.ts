/**
 * Куда записывается связка материала с документом качества (issue #681).
 *
 * Вкладка ЧИТАЕТ связки всех четырёх уровней и показывает победившую (узкий уровень перекрывает
 * широкий), а писала всегда в область из локального селектора. Команда апсертит по тройке
 * (область, объект области, ключ материала) — то есть запись с ДРУГОЙ областью не перенацеливает
 * существующую связку, а заводит вторую рядом. Старая продолжает действовать в других комплектах,
 * хотя на экране всё выглядит так, будто связку заменили. В текущем комплекте подмена даже
 * незаметна: при генерации побеждает более узкая, то есть новая.
 *
 * Правило поэтому такое: у строки со связкой область берётся у ЭТОЙ связки, у строки без связки —
 * из селектора. Экран контроля связок пришёл к тому же и по той же причине (`relinkMany`).
 *
 * Здесь только группировка — без React, чтобы правило проверялось тестами, а не глазами.
 */
import type { CatalogScope } from '@/shared/api/types';

/** Область и объект области: то, чем адресуется связка (System живёт без объекта). */
export interface LinkScope { scope: CatalogScope; scopeId: string | null }

/** Материалы, которые можно отправить одним вызовом: у них общая область. */
export interface LinkTargetGroup<T> extends LinkScope { materials: T[] }

function sameScope(a: LinkScope, b: LinkScope): boolean {
  return a.scope === b.scope && (a.scopeId ?? null) === (b.scopeId ?? null);
}

/**
 * Разбивает материалы на группы по области записи: строка со связкой идёт в область своей связки,
 * строка без связки — в `fallback` (область из селектора).
 *
 * Порядок групп — порядок первого материала: сообщения об ошибке и счётчики читаются человеком в
 * том же порядке, в каком он видел строки.
 */
export function groupByTargetScope<T>(
  rows: readonly T[],
  existingScopeOf: (row: T) => LinkScope | undefined,
  fallback: LinkScope,
): LinkTargetGroup<T>[] {
  const groups: LinkTargetGroup<T>[] = [];
  for (const row of rows) {
    const target = existingScopeOf(row) ?? fallback;
    const group = groups.find(g => sameScope(g, target));
    if (group) group.materials.push(row);
    else groups.push({ scope: target.scope, scopeId: target.scopeId ?? null, materials: [row] });
  }
  return groups;
}

/**
 * Нужна ли для этой отправки область из селектора. Если все строки идут в области своих связок,
 * готовность селектора не важна — и требовать её значило бы запрещать перепривязку, пока комплект
 * догружается, хотя писать эта перепривязка будет совсем не туда.
 */
export function needsFallbackScope<T>(
  rows: readonly T[],
  existingScopeOf: (row: T) => LinkScope | undefined,
): boolean {
  return rows.some(row => !existingScopeOf(row));
}
