import type { DocumentType } from '@/shared/api/types';
import type { QualityDocument } from '@/shared/api/qualityDocs';
import { findTaggedFieldPath } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';

/**
 * Чем один документ качества отличается от другого (issue #588).
 *
 * Живой случай: два сертификата назывались одинаково — «EKF — автоматические выключатели», а внутри
 * разные номера (RU C-CN.HA46.B.06753/23 и ЕАЭС RU C-CN.АД07.B.05521/23), разные органы и разные
 * области продукции (AV-125 против AV-6 и AV-10). В списке они неразличимы, и человек выбирает
 * вслепую — а выбирает он, какой сертификат уедет в исполнительную документацию.
 *
 * Реквизиты читаются по функциональным тэгам, а не по именам полей: у разных типов документов
 * качества поля называются по-разному.
 */

function readPath(obj: Record<string, unknown>, path: string[]): unknown {
  return path.reduce<unknown>((o, k) => (o && typeof o === 'object') ? (o as Record<string, unknown>)[k] : undefined, obj);
}

/** Значение поля документа по функциональному тэгу; пусто, если тэг не проставлен или поля нет. */
export function docFieldByTag(doc: QualityDocument, docTypes: DocumentType[], tag: string): string {
  const dt = docTypes.find(t => t.id === doc.documentTypeId);
  const path = dt ? findTaggedFieldPath(dt, tag, docTypes) : null;
  const v = path ? readPath(doc.requisites, path) : undefined;
  return typeof v === 'string' ? v.trim() : '';
}

export const docNumberOf = (doc: QualityDocument, docTypes: DocumentType[]) =>
  docFieldByTag(doc, docTypes, FUNCTIONAL_TAG.docNumber);

export const docValidUntilOf = (doc: QualityDocument, docTypes: DocumentType[]) =>
  docFieldByTag(doc, docTypes, FUNCTIONAL_TAG.qualityValidUntil);

/**
 * Имена, встречающиеся у ДВУХ И БОЛЕЕ документов списка (сравнение как у серверной проверки
 * уникальности: без регистра и краевых пробелов).
 *
 * Нужны, чтобы приписывать номер только там, где имя действительно не различает: приписывать всем
 * значит засорить список ради редкого случая, и тогда номер перестанут замечать ровно тогда, когда
 * он понадобится.
 */
export function ambiguousDocNames(docs: readonly QualityDocument[]): Set<string> {
  const seen = new Map<string, number>();
  for (const d of docs) {
    const key = d.displayName.trim().toLowerCase();
    seen.set(key, (seen.get(key) ?? 0) + 1);
  }
  return new Set([...seen].filter(([, n]) => n > 1).map(([name]) => name));
}

/** Одноимённый ли документ в этом списке — значит имени недостаточно, нужен номер. */
export function isAmbiguous(doc: QualityDocument, ambiguous: Set<string>): boolean {
  return ambiguous.has(doc.displayName.trim().toLowerCase());
}
