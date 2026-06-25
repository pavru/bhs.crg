import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { Construction, Section, DocumentSet } from './types';

const KEYS = {
  list: ['constructions'] as const,
  detail: (id: string) => ['constructions', id] as const,
};

// ── Constructions ──────────────────────────────────────────────────────────────

export function useListConstructions() {
  return useQuery({
    queryKey: KEYS.list,
    queryFn: () => apiClient.get<Construction[]>('/constructions').then(r => r.data),
  });
}

export function useGetConstruction(id: string) {
  return useQuery({
    queryKey: KEYS.detail(id),
    queryFn: () => apiClient.get<Construction>(`/constructions/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

export function useCreateConstruction() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) =>
      apiClient.post<Construction>('/constructions', { name }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.list }),
  });
}

export function useRenameConstruction() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      apiClient.put<Construction>(`/constructions/${id}`, { name }).then(r => r.data),
    onSuccess: (_, { id }) => {
      qc.invalidateQueries({ queryKey: KEYS.list });
      qc.invalidateQueries({ queryKey: KEYS.detail(id) });
    },
  });
}

export function useDeleteConstruction() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/constructions/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.list }),
  });
}

// ── Sections ───────────────────────────────────────────────────────────────────

export function useCreateSection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ constructionId, name }: { constructionId: string; name: string }) =>
      apiClient.post<Section>(`/constructions/${constructionId}/sections`, { name }).then(r => r.data),
    onSuccess: (s) => qc.invalidateQueries({ queryKey: KEYS.detail(s.constructionId) }),
  });
}

export function useRenameSection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      apiClient.put<Section>(`/sections/${id}`, { name }).then(r => r.data),
    onSuccess: (s) => qc.invalidateQueries({ queryKey: KEYS.detail(s.constructionId) }),
  });
}

export function useDeleteSection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, constructionId }: { id: string; constructionId: string }) =>
      apiClient.delete(`/sections/${id}`).then(() => constructionId),
    onSuccess: (constructionId) => qc.invalidateQueries({ queryKey: KEYS.detail(constructionId) }),
  });
}

// ── DocumentSets ───────────────────────────────────────────────────────────────

export function useCreateDocumentSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ sectionId, name, constructionId }: { sectionId: string; name: string; constructionId: string }) =>
      apiClient.post<DocumentSet>(`/sections/${sectionId}/sets`, { name }).then(r => ({ ...r.data, _constructionId: constructionId })),
    onSuccess: (data) => qc.invalidateQueries({ queryKey: KEYS.detail((data as any)._constructionId) }),
  });
}

export function useRenameDocumentSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name, constructionId }: { id: string; name: string; constructionId: string }) =>
      apiClient.put<DocumentSet>(`/document-sets/${id}`, { name }).then(r => ({ ...r.data, _constructionId: constructionId })),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: KEYS.detail((data as any)._constructionId) });
      qc.invalidateQueries({ queryKey: ['document-sets', data.id] });
    },
  });
}

export function useDeleteDocumentSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, constructionId }: { id: string; constructionId: string }) =>
      apiClient.delete(`/document-sets/${id}`).then(() => constructionId),
    onSuccess: (constructionId) => qc.invalidateQueries({ queryKey: KEYS.detail(constructionId) }),
  });
}
