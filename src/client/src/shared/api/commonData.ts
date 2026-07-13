import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogScope, CommonDataEntry, CommonDataEntryWithScope } from './types';

const QK = 'common-data';

export function useListCommonData(params?: {
  scope?: CatalogScope;
  scopeId?: string;
  typeId?: string;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, { scope: params?.scope, scopeId: params?.scopeId, typeId: params?.typeId }],
    queryFn: () =>
      apiClient
        .get<CommonDataEntry[]>('/common-data', { params: {
          scope: params?.scope,
          scopeId: params?.scopeId,
          typeId: params?.typeId,
        }})
        .then(r => r.data),
    enabled: params?.enabled !== false,
  });
}

export function useCommonDataForSet({
  setId,
  typeId,
  enabled = true,
}: {
  setId: string | undefined;
  typeId?: string;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, 'for-set', setId, typeId ?? null],
    queryFn: () =>
      apiClient
        .get<CommonDataEntryWithScope[]>(`/common-data/for-set/${setId}`, {
          params: typeId ? { typeId } : undefined,
        })
        .then(r => r.data),
    enabled: enabled && !!setId,
  });
}

/**
 * Записи, видимые из ЛЮБОГО скопа (issue #82): резолвит родительскую цепочку
 * (Раздел→Стройка→Система и т.д.) — в отличие от useCommonDataForSet, который стартует с комплекта.
 */
export function useCommonDataForScope({
  scope,
  scopeId,
  typeId,
  enabled = true,
}: {
  scope: CatalogScope | undefined;
  scopeId?: string | null;
  typeId?: string;
  enabled?: boolean;
}) {
  return useQuery({
    queryKey: [QK, 'for-scope', scope ?? null, scopeId ?? null, typeId ?? null],
    queryFn: () =>
      apiClient
        .get<CommonDataEntryWithScope[]>('/common-data/for-scope', {
          params: { scope, scopeId: scopeId ?? undefined, typeId },
        })
        .then(r => r.data),
    enabled: enabled && !!scope,
  });
}

/** Одна запись каталога по id — для показа резолвнутой $ref-ссылки в связанном поле (issue #99). */
export function useCommonDataEntry(id: string | undefined) {
  return useQuery({
    queryKey: [QK, 'by-id', id],
    queryFn: () => apiClient.get<CommonDataEntry>(`/common-data/${id}`).then(r => r.data),
    enabled: !!id,
    staleTime: 60_000,
  });
}

/** Проверка связок (issue #99): статус каждого @@ref-поля. */
export interface BindingCheckItem {
  fieldKey: string;
  fieldTitle: string;
  status: 'matched' | 'not-found' | 'dangling' | 'drift' | 'stale';
  linkedName: string | null;
  detail: string | null;
}

/** По требованию (кнопка «Проверить связки») — enabled:false, дёргается через refetch. */
export function useCheckBindings(id: string | undefined) {
  return useQuery({
    queryKey: [QK, 'binding-check', id],
    queryFn: () => apiClient.get<{ items: BindingCheckItem[] }>(`/common-data/${id}/binding-check`).then(r => r.data),
    enabled: false,
  });
}

export function useCreateCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: {
      displayName: string;
      compositeTypeId: string;
      data: string;
      scope: CatalogScope;
      scopeId?: string | null;
      aliases?: string[];
    }) => apiClient.post<CommonDataEntry>('/common-data', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useUpdateCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, displayName, data, aliases }: { id: string; displayName: string; data: string; aliases?: string[] }) =>
      apiClient.put<CommonDataEntry>(`/common-data/${id}`, { displayName, data, aliases }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useDeleteCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/common-data/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}
