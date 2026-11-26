import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiError, withBlobErrorBody } from '@/shared/utils/apiError';
import { apiClient } from './client';
import type { Schedule, ScheduleCreate, ScheduleLinkInput, ScheduleRebuildPreview, ScheduleSummary, ScheduleWorkInput,
  ScheduleWorkRef } from './planningScheduleTypes';

const PATH = '/plan/schedules';
export const scheduleKey = ['planning', 'schedules'] as const;
const siteKey = (constructionId: string) => [...scheduleKey, constructionId] as const;
const detailKey = (constructionId: string, id: string) => [...siteKey(constructionId), id] as const;
const match = (version: string) => ({ headers: { 'If-Match': version } });

export async function previewSchedulePrint(id: string, signal?: AbortSignal) {
  const path = `/print/${encodeURIComponent('ГрафикСтройки')}/${id}/preview`;
  const response = await withBlobErrorBody(apiClient.post<Blob>(path, undefined, { signal, responseType: 'blob' }));
  return response.data;
}

function rereadAfterDeleteRefusal(client: QueryClient, constructionId: string, error: unknown) {
  const status = (error as { response?: { status?: number } } | null)?.response?.status;
  if (status === 409 || status === 404) return client.invalidateQueries({ queryKey: siteKey(constructionId) });
}

export function useSchedules(constructionId: string, enabled: boolean) {
  return useQuery({
    queryKey: siteKey(constructionId),
    queryFn: ({ signal }) => apiClient.get<ScheduleSummary[]>(PATH, { params: { constructionId }, signal }).then(r => r.data),
    enabled,
  });
}

export function useSchedule(constructionId: string, id: string, enabled = true) {
  return useQuery({
    queryKey: detailKey(constructionId, id),
    queryFn: ({ signal }) => apiClient.get<Schedule>(`${PATH}/${id}`, { signal }).then(r => r.data),
    enabled,
  });
}

type LinkWrite =
  | { kind: 'add'; input: ScheduleLinkInput }
  | { kind: 'edit'; id: string; version: string; input: ScheduleLinkInput }
  | { kind: 'delete'; id: string; version: string };

export function useWriteScheduleLink(constructionId: string, id: string) {
  const client = useQueryClient();
  const path = `${PATH}/${id}/links`;
  return useMutation({
    mutationFn: async (write: LinkWrite) => {
      if (write.kind === 'add') {
        await apiClient.post(path, write.input);
      } else if (write.kind === 'edit') {
        await apiClient.put(`${path}/${write.id}`, write.input, match(write.version));
      } else {
        await apiClient.delete(`${path}/${write.id}`, match(write.version));
      }
    },
    onSuccess: () => client.invalidateQueries({ queryKey: siteKey(constructionId) }),
    onError: (error, write) => write.kind === 'delete' ? rereadAfterDeleteRefusal(client, constructionId, error) : undefined,
  });
}

export function useCreateSchedule() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: ScheduleCreate) => apiClient.post<Schedule>(PATH, input).then(r => r.data),
    onSuccess: schedule => {
      void client.invalidateQueries({ queryKey: siteKey(schedule.constructionId), exact: true });
      void client.invalidateQueries({ queryKey: detailKey(schedule.constructionId, schedule.id), exact: true });
      if (schedule.revisionId) void client.invalidateQueries({ queryKey: ['planning', 'estimates'] });
    },
  });
}

export type WorkWrite =
  | { kind: 'add'; workTypeId: string; sectionId: string | null; input: ScheduleWorkInput }
  | { kind: 'edit'; id: string; version: string; input: ScheduleWorkInput }
  | { kind: 'delete'; id: string; version: string };

export function useWriteScheduleWork(constructionId: string, id: string) {
  const client = useQueryClient();
  const path = `${PATH}/${id}/works`;
  return useMutation({
    mutationFn: async (write: WorkWrite) => {
      if (write.kind === 'add') {
        await apiClient.post(path, { workTypeId: write.workTypeId, sectionId: write.sectionId, work: write.input });
      } else if (write.kind === 'edit') {
        await apiClient.put(`${path}/${write.id}`, write.input, match(write.version));
      } else {
        await apiClient.delete(`${path}/${write.id}`, match(write.version));
      }
    },
    // Полный ответ заново читает предупреждения у обеих работ, в том числе в соседних графиках.
    onSuccess: () => client.invalidateQueries({ queryKey: siteKey(constructionId) }),
    onError: (error, write) => write.kind === 'delete' ? rereadAfterDeleteRefusal(client, constructionId, error) : undefined,
  });
}

export async function readScheduleRebuild(id: string, signal?: AbortSignal) {
  return (await apiClient.get<ScheduleRebuildPreview>(`${PATH}/${id}/rebuild-preview`, { signal })).data;
}

export function useRebuildSchedule(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ version, fingerprint }: ScheduleRebuildPreview) =>
      apiClient.post<Schedule>(`${PATH}/${id}/rebuild`, { fingerprint }, match(version)).then(r => r.data),
    // Текущий факт и диагностику возвращает чтение графика. Оно обновляет кэш после записи.
    onSuccess: schedule => client.invalidateQueries({ queryKey: siteKey(schedule.constructionId) }),
  });
}

export type ScheduleRefusal =
  | { kind: 'preview-changed'; message: string; preview: ScheduleRebuildPreview }
  | { kind: 'cycle'; message: string; cycle: ScheduleWorkRef[] }
  | { kind: 'validation' | 'forbidden' | 'conflict' | 'other'; message: string };

export function scheduleRefusal(error: unknown, fallback: string): ScheduleRefusal {
  const response = (error as { response?: { status?: number; data?: {
    code?: string; cycle?: ScheduleWorkRef[]; preview?: ScheduleRebuildPreview;
  } } } | null)?.response;
  const message = apiError(error, fallback);
  if (response?.status === 403) return { kind: 'forbidden', message: 'Для этого действия недостаточно прав.' };
  if (response?.status === 409) {
    const preview = response.data?.preview;
    if (preview?.version && preview.fingerprint && preview.skipped &&
      [preview.added, preview.changed, preview.removed, preview.removedLinks].every(Array.isArray)) {
      return { kind: 'preview-changed', message, preview };
    }
    return { kind: 'conflict', message };
  }
  if (response?.status === 422 && response.data?.code === 'schedule-cycle' && Array.isArray(response.data.cycle)) {
    return { kind: 'cycle', message, cycle: response.data.cycle };
  }
  if (response?.status === 400 || response?.status === 422) return { kind: 'validation', message };
  return { kind: 'other', message };
}
