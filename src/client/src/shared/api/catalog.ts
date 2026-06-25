import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogEntity } from './types';

export function useListCatalogEntities(entityType?: string) {
  return useQuery({
    queryKey: ['catalog', entityType ?? ''],
    queryFn: () =>
      apiClient
        .get<CatalogEntity[]>('/catalog', { params: entityType ? { entityType } : undefined })
        .then((r) => r.data),
  });
}

export function useCreateCatalogEntity() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { entityType: string; displayName: string; data: string; ownerId?: string }) =>
      apiClient.post<CatalogEntity>('/catalog', payload).then((r) => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['catalog'] }),
  });
}

export function useUpdateCatalogEntity() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, ...payload }: { id: string; displayName: string; data: string }) =>
      apiClient.put<CatalogEntity>(`/catalog/${id}`, payload).then((r) => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['catalog'] }),
  });
}

export function useDeleteCatalogEntity() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/catalog/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['catalog'] }),
  });
}
