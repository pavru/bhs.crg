import { useState } from 'react';
import type { CommonDataEntry, CatalogScope } from '@/shared/api/types';

/**
 * Общая подача каталога общих данных в диалогах выбора (issue #751).
 *
 * <p>К одной и той же строке union-массива ведут два входа — «Из каталога» в шапке массива и
 * «Выбрать документ…» внутри варианта, — и один и тот же каталог они показывали по-разному:
 * первый группами по уровню с бейджами, второй плоским списком с уровнем в подписи. Множество
 * записей при этом совпадает (оба эндпоинта, <code>for-set</code> и <code>for-scope</code>, идут
 * вверх по цепочке Комплект→Раздел→Стройка→Система), так что человек видел ОДНИ И ТЕ ЖЕ записи
 * дважды по-разному и не мог сопоставить одно с другим.</p>
 *
 * <p>Разделено на хук и разметку, потому что вызывающий обязан знать, какие записи ВИДНЫ: свёрнутая
 * группа не должна попадать в клавиатурную навигацию, иначе стрелки уходят на спрятанное. Общий
 * компонент, прячущий сворачивание внутри, такой список отдать бы не смог.</p>
 */
const SCOPE_ORDER: CatalogScope[] = ['Set', 'Section', 'Construction', 'System'];

export type ScopeGroup = { scope: CatalogScope; entries: CommonDataEntry[] };

export function useScopeGroups(entries: CommonDataEntry[], searching: boolean) {
  const groups: ScopeGroup[] = SCOPE_ORDER
    .map(scope => ({ scope, entries: entries.filter(e => e.scope === scope) }))
    .filter(g => g.entries.length > 0);

  const firstScope = groups[0]?.scope;   // ближайшая НЕпустая группа раскрыта по умолчанию
  // Ручные переопределения действуют, пока поиск пуст: при поиске раскрыто всё, иначе совпадение
  // спряталось бы за свёрнутой группой.
  const [override, setOverride] = useState<Partial<Record<CatalogScope, boolean>>>({});
  const isExpanded = (scope: CatalogScope) =>
    searching ? true : (override[scope] ?? scope === firstScope);
  const toggle = (scope: CatalogScope) =>
    setOverride(o => ({ ...o, [scope]: !isExpanded(scope) }));

  /** Записи раскрытых групп в порядке отображения — ровно то, по чему ходят стрелки. */
  const visible = groups.flatMap(g => (isExpanded(g.scope) ? g.entries : []));

  return { groups, isExpanded, toggle, visible };
}
