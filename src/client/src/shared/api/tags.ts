import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/** Коды функциональных тэгов, на которые фронтенд завязан напрямую (зеркало FunctionalTag). */
export const FUNCTIONAL_TAG = {
  docPrintForm: 'doc.printForm',
  docPageCount: 'doc.pageCount',
  docNumber: 'doc.number',
  typeQualityDocument: 'type.qualityDocument',
  typeProjectDocumentation: 'type.projectDocumentation',
  materialQualityDocLink: 'material.qualityDocLink',
  identity: 'identity',
  qualityValidUntil: 'quality.validUntil',
  qualityManufacturer: 'quality.manufacturer',
  datasetHasCover: 'dataset.hasCover',
  datasetHasTitlePage: 'dataset.hasTitlePage',
  datasetHasTitleBlock: 'dataset.hasTitleBlock',
} as const;

export type TagScope = 'Field' | 'Type' | 'Dataset' | 'GostDocument';

export interface TagDefinition {
  code: string;
  label: string;
  description: string;
  scope: TagScope;
  /** For Field: allowed SchemaField.type values; for Type: allowed kinds ("Document"/"Composite"). Empty = any. */
  appliesTo: string[];
  multiple: boolean;
}

export function useTagRegistry() {
  return useQuery({
    queryKey: ['tag-registry'],
    queryFn: () => apiClient.get<TagDefinition[]>('/tags').then(r => r.data),
    staleTime: 5 * 60_000,
  });
}

export function fieldTags(all: TagDefinition[] | undefined, fieldType: string): TagDefinition[] {
  return (all ?? []).filter(t => t.scope === 'Field' && (t.appliesTo.length === 0 || t.appliesTo.includes(fieldType)));
}

export function typeTags(all: TagDefinition[] | undefined, kind: string): TagDefinition[] {
  // Type-тэги + GostDocument-тэги (issue #29): пометив тип тэгом таблицы (Спецификация/Кабельный
  // журнал), админ объявляет его целевым типом материализации для таких таблиц ГОСТ-документов.
  return (all ?? []).filter(t => (t.scope === 'Type' || t.scope === 'GostDocument')
    && (t.appliesTo.length === 0 || t.appliesTo.includes(kind)));
}

export function datasetTags(all: TagDefinition[] | undefined): TagDefinition[] {
  return (all ?? []).filter(t => t.scope === 'Dataset');
}
