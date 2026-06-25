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

export function useCreateCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: {
      displayName: string;
      compositeTypeId: string;
      data: string;
      scope: CatalogScope;
      scopeId?: string | null;
    }) => apiClient.post<CommonDataEntry>('/common-data', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useUpdateCommonDataEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, displayName, data }: { id: string; displayName: string; data: string }) =>
      apiClient.put<CommonDataEntry>(`/common-data/${id}`, { displayName, data }).then(r => r.data),
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
