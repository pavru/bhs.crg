import { apiClient } from '@/shared/api/client';
import { recognizeDocument, type QualityDocument } from '@/shared/api/qualityDocs';
import type { DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields, findTaggedFieldPath } from '@/shared/api/schema';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { flattenLeaves, applyRecognized } from './QualityDocForm';

const SUMMARY = '__summary__';

/**
 * Распознаёт скан импортированного документа качества и обновляет его реквизиты,
 * краткое наименование и количество листов. Тип документа уже задан при импорте
 * (классификация не нужна). Best-effort: при ошибке распознавания возвращает исходный документ.
 */
export async function recognizeAndUpdate(doc: QualityDocument, allDocTypes: DocumentType[]): Promise<QualityDocument> {
  if (!doc.scanBlobPath || !doc.scanMimeType) return doc;
  const type = allDocTypes.find(t => t.id === doc.documentTypeId);
  if (!type) return doc;

  const fields = flattenLeaves(resolveEffectiveFields(type, allDocTypes), allDocTypes);
  const rec = await recognizeDocument({
    blobPath: doc.scanBlobPath,
    mimeType: doc.scanMimeType,
    fields: [
      ...fields,
      { path: SUMMARY, title: 'Краткое наименование документа: краткое имя (бренд) производителя и тип продукции, например «EKF — автоматические выключатели»', type: 'string' },
    ],
  });

  const summary = (rec.values[SUMMARY] ?? '').trim();
  const { [SUMMARY]: _omit, ...fieldValues } = rec.values;
  if (rec.pageCount != null) {
    const p = findTaggedFieldPath(type, FUNCTIONAL_TAG.docPageCount, allDocTypes);
    if (p) fieldValues[p.join('.')] = String(rec.pageCount);
  }

  const requisites = applyRecognized(doc.requisites, fieldValues);
  const displayName = summary || doc.displayName;
  const { data } = await apiClient.put<QualityDocument>(`/quality-docs/${doc.id}`, {
    documentTypeId: doc.documentTypeId, displayName, requisites,
  });
  return data;
}
