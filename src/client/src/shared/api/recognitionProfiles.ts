import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { ruCount } from '@/shared/utils/pluralize';
import type { HiddenRecognitionProfile } from './recognitionProfileGroups';

/**
 * Профили распознавания (issue #405/#408): промпты остаются в коде, профиль задаёт к ним параметры.
 * Всё, что UI знает о видах, приходит с сервера в kindInfo — на клиенте частных случаев вида нет.
 */

/** Поле/колонка профиля. Описание — смысловая подсказка модели, а не украшение. */
export interface RecognitionProfileField {
  name: string;
  description?: string | null;
  /** string | number | date; пусто = string. */
  type?: string | null;
  options?: string[] | null;
}

/** Структурные подсказки о форме таблицы (закрытый набор — свободного текста промпта нет). */
export interface RecognitionTableShape {
  twoTierHeader: boolean;
  pairedSections: boolean;
  skipTotals: boolean;
}

/** Что вид означает для UI — источник истины на сервере. */
export interface RecognitionKindInfo {
  kind: string;
  label: string;
  supportsShape: boolean;
  hasScalarFields: boolean;
  isTabular: boolean;
  /** Поля, на которых завязан код: удалять/переименовывать нельзя. */
  systemFieldNames: string[];
  /** Куда привязывается: 'File' (набор целиком) или 'PageGroup' (группа листов). */
  scope: 'File' | 'PageGroup';
  /** Владелец вида: код модуля или «core» (issue #1075). По нему виды складываются в группы. */
  module?: string | null;
  moduleTitle?: string | null;
}

export interface RecognitionProfile {
  id: string;
  name: string;
  /** Код есть только у встроенных профилей. */
  code: string | null;
  kind: string;
  fields: RecognitionProfileField[];
  rowColumns: RecognitionProfileField[];
  shape: RecognitionTableShape | null;
  isBuiltIn: boolean;
  isModified: boolean;
  /** Заводская версия ушла вперёд, а правка пользователя сохранена. */
  builtInOutdated: boolean;
  kindInfo: RecognitionKindInfo;
  /** Владелец ПРОФИЛЯ — по нему профили складываются в группы рейла (issue #1075). */
  module?: string | null;
  moduleTitle?: string | null;
}

/** Профиль PDF для диалога «Распознать PDF» — перечень и тексты отдаёт сервер (issue #1075). */
export interface PdfProfileInfo {
  /** Значение, которое уходит в `POST …/pdf-sources`. */
  profile: string;
  title: string;
  nameHint: string;
  summary: string;
  /** Спрашивать ли тэги структуры PDF (обложка, титульный лист). */
  structureTags: boolean;
}

export interface RecognitionProfileInput {
  name: string;
  kind?: string;
  fields: RecognitionProfileField[];
  rowColumns: RecognitionProfileField[];
  shape: RecognitionTableShape | null;
}

const KEY = ['recognition-profiles'];

export function useListRecognitionProfiles() {
  return useQuery<RecognitionProfile[]>({
    queryKey: KEY,
    queryFn: () => apiClient.get('/recognition-profiles').then(r => r.data),
  });
}

export function useRecognitionKinds() {
  return useQuery<RecognitionKindInfo[]>({
    queryKey: [...KEY, 'kinds'],
    queryFn: () => apiClient.get('/recognition-profiles/kinds').then(r => r.data),
    staleTime: Infinity, // виды заданы кодом — за сессию не меняются
  });
}

/**
 * Профили выключенных модулей — то, что общий список не отдаёт (issue #1075). Нужны, чтобы назвать
 * причину: «скрыто столько-то» на экране профилей и «модуль выключен» у привязки набора.
 */
export function useHiddenRecognitionProfiles() {
  return useQuery<HiddenRecognitionProfile[]>({
    queryKey: [...KEY, 'hidden'],
    queryFn: () => apiClient.get('/recognition-profiles/hidden').then(r => r.data),
  });
}

/** Что предложить в диалоге «Распознать PDF»: только то, что сервер на этом экземпляре примет. */
export function usePdfProfiles() {
  return useQuery<PdfProfileInfo[]>({
    queryKey: [...KEY, 'pdf'],
    queryFn: () => apiClient.get('/recognition-profiles/pdf').then(r => r.data),
  });
}

export function useCreateRecognitionProfile() {
  const qc = useQueryClient();
  return useMutation<RecognitionProfile, Error, RecognitionProfileInput>({
    mutationFn: input => apiClient.post('/recognition-profiles', input).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useUpdateRecognitionProfile() {
  const qc = useQueryClient();
  return useMutation<RecognitionProfile, Error, { id: string } & RecognitionProfileInput>({
    mutationFn: ({ id, ...input }) => apiClient.put(`/recognition-profiles/${id}`, input).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useResetRecognitionProfile() {
  const qc = useQueryClient();
  return useMutation<RecognitionProfile, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.post(`/recognition-profiles/${id}/reset`).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useDeleteRecognitionProfile() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.delete(`/recognition-profiles/${id}`).then(() => undefined),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

/** Короткое превью параметров для строки списка. */
export function profileSummary(p: RecognitionProfile): string {
  const parts: string[] = [];
  if (p.fields.length > 0) parts.push(ruCount(p.fields.length, 'поле', 'поля', 'полей'));
  if (p.rowColumns.length > 0) parts.push(ruCount(p.rowColumns.length, 'колонка', 'колонки', 'колонок'));
  return parts.length > 0 ? parts.join(' · ') : 'параметров нет';
}
