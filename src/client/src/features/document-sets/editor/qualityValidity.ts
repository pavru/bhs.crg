import { findTaggedFieldPath } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { QualityDocument } from '@/shared/api/qualityDocs';
import type { DocumentType } from '@/shared/api/types';

/**
 * Срок действия документа качества — по функциональному тэгу `quality.validUntil` (issue #1032).
 *
 * Отдельным модулем от компонентов, а не ради опрятности: это смысл, а не оформление. Ошибка здесь
 * молча помечает просроченный сертификат годным — и в PDF уходит документ, которого в деле быть не
 * должно. Внутри вкладки эти функции были непроверяемы (файл экспортировал компоненты, тестам
 * достать их было неоткуда); здесь у них есть тест — `qualityValidity.test.ts`.
 */
function readPath(obj: Record<string, unknown>, path: string[]): unknown {
  return path.reduce<unknown>((o, k) => (o && typeof o === 'object') ? (o as Record<string, unknown>)[k] : undefined, obj);
}

export function getValidUntil(doc: QualityDocument, allDocTypes: DocumentType[]): string | null {
  const dt = allDocTypes.find(t => t.id === doc.documentTypeId);
  if (!dt) return null;
  const path = findTaggedFieldPath(dt, FUNCTIONAL_TAG.qualityValidUntil, allDocTypes);
  if (!path) return null;
  const v = readPath(doc.requisites, path);
  return typeof v === 'string' && v.trim() ? v : null;
}

export function isExpired(doc: QualityDocument, allDocTypes: DocumentType[]): boolean {
  const vu = getValidUntil(doc, allDocTypes);
  if (!vu) return false; // нет даты — не считаем просроченным
  const d = new Date(vu);
  if (Number.isNaN(d.getTime())) return false;
  const today = new Date(); today.setHours(0, 0, 0, 0);
  return d < today;
}
