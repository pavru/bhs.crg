import type { CatalogScope, DocumentType } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export type BaseCandidateKind = 'instance' | 'catalog';

export interface BaseCandidate {
  kind: BaseCandidateKind;
  id: string;
  name: string;
  typeId: string;
  tier: number;        // скоп-уровень: 0 комплект, 1 раздел, 2 стройка, 3 система
  scopeLabel: string;  // «Комплект»/«Раздел»/«Стройка»/«Система»
  dist: number;        // дистанция наследования: 0 прямой родитель, дальше — больше
  proxy?: boolean;     // issue #89: кандидат того же типа — «роль на реальный объект», а не наследование от родителя
  targetIsProxy?: boolean; // выбранный «реальный» сам является прокси (цепочка ссылок — data-smell)
}

export const SCOPE_TIER: Record<CatalogScope, number> = { Set: 0, Section: 1, Construction: 2, System: 3 };

/** Идентификаторы типов-предков по цепочке parentId (по возрастанию дистанции: [родитель, дед, …]). */
export function ancestorTypeIds(docType: DocumentType | undefined, allDocTypes: DocumentType[]): string[] {
  const ids: string[] = [];
  const seen = new Set<string>();
  let cur = docType?.parentId ?? undefined;
  while (cur && !seen.has(cur)) {
    seen.add(cur); ids.push(cur);
    cur = allDocTypes.find(dt => dt.id === cur)?.parentId ?? undefined;
  }
  return ids;
}

/** Толерантный разбор _baseRef: {kind,id} (issue #71) или голая строка-id (legacy = catalog/запись). */
export function parseBaseRef(raw: unknown): { kind: BaseCandidateKind; id: string } | undefined {
  if (typeof raw === 'string') return raw ? { kind: 'catalog', id: raw } : undefined;
  if (raw && typeof raw === 'object' && 'id' in raw) {
    const r = raw as { kind?: string; id?: string };
    if (r.id) return { kind: r.kind === 'instance' ? 'instance' : 'catalog', id: r.id };
  }
  return undefined;
}
