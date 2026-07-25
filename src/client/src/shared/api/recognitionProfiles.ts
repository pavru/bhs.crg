import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

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
  if (p.fields.length > 0) parts.push(`${p.fields.length} ${plural(p.fields.length, 'поле', 'поля', 'полей')}`);
  if (p.rowColumns.length > 0) parts.push(`${p.rowColumns.length} ${plural(p.rowColumns.length, 'колонка', 'колонки', 'колонок')}`);
  return parts.length > 0 ? parts.join(' · ') : 'параметров нет';
}

function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}
