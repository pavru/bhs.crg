import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/** Коды функциональных тэгов, на которые фронтенд завязан напрямую (зеркало FunctionalTag). */
export const FUNCTIONAL_TAG = {
  docPrintForm: 'doc.printForm',
  docPageCount: 'doc.pageCount',
  docNumber: 'doc.number',
  typeQualityDocument: 'type.qualityDocument',
  typeProjectDocumentation: 'type.projectDocumentation',
  typeUnion: 'type.union',
  materialQualityDocLink: 'material.qualityDocLink',
  identity: 'identity',
  qualityValidUntil: 'quality.validUntil',
  qualityManufacturer: 'quality.manufacturer',
  datasetHasCover: 'dataset.hasCover',
  datasetHasTitlePage: 'dataset.hasTitlePage',
  datasetHasTitleBlock: 'dataset.hasTitleBlock',
  profileConstruction: 'profile.construction',
  profileSection: 'profile.section',
  profileSet: 'profile.set',
} as const;

/**
 * Разбор записи тэга в схеме: «код» либо «код:параметр» (issue #583) — зеркало серверного TagCode.
 *
 * Двоеточие выбрано разделителем потому, что в кодах тэгов его нет (там точки), поэтому разбор
 * однозначен, а запись без параметра остаётся собой. Нечисловой или отрицательный параметр — не
 * ошибка, а просто «без номера»: опечатка в номере не должна молча отключать поле от сопоставления.
 */
export function tagCode(raw: string): string {
  const value = raw.trim();
  const sep = value.indexOf(':');
  return sep < 0 ? value : value.slice(0, sep).trim();
}

/** Числовой параметр тэга или null, если его нет (см. {@link tagCode}). */
export function tagOrder(raw: string): number | null {
  const sep = raw.indexOf(':');
  if (sep < 0) return null;
  const param = raw.slice(sep + 1).trim();
  return /^\d+$/.test(param) ? Number(param) : null;
}

/** Несёт ли набор тэгов указанный КОД — с параметром или без («identity» найдёт и «identity:2»). */
export function hasTag(tags: string[] | undefined, code: string): boolean {
  return !!tags?.some(t => tagCode(t) === code);
}

/** Запись тэга с указанным кодом как она лежит в схеме («identity:2»), если тэг проставлен. */
export function findTagEntry(tags: string[] | undefined, code: string): string | undefined {
  return tags?.find(t => tagCode(t) === code);
}

/**
 * Меняет параметр уже проставленного тэга, сохраняя его место в списке: null убирает номер.
 * Порядок записей в схеме не меняем — тэг не должен «прыгать» в форме от правки номера.
 */
export function withTagOrder(tags: string[], code: string, order: number | null): string[] {
  return tags.map(t => (tagCode(t) === code ? (order === null ? code : `${code}:${order}`) : t));
}

/** Внутреннее ограничение тэга (issue #258): глобальный максимум носителей и т.п. */
export interface TagRestriction {
  maxBearers?: number | null;
}

/** Описание числового параметра тэга (issue #583). Есть только у тэгов, которые его принимают. */
export interface TagParameter {
  label: string;
  description: string;
}

export type TagScope = 'Field' | 'Type' | 'Dataset' | 'GostDocument';

export interface TagDefinition {
  code: string;
  label: string;
  description: string;
  scope: TagScope;
  /** For Field: allowed SchemaField.type values; for Type: allowed kinds ("Document"/"Composite"). Empty = any. */
  appliesTo: string[];
  multiple: boolean;
  /** Внутреннее ограничение назначения (напр. глобальный максимум носителей). */
  restriction?: TagRestriction | null;
  /** Числовой параметр тэга — есть только у тех, кто его принимает (напр. «identity:1»). */
  parameter?: TagParameter | null;
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
