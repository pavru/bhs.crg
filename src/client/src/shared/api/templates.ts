import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { Template } from './types';

export function useListTemplates(documentTypeId: string | undefined) {
  return useQuery({
    queryKey: ['templates', documentTypeId],
    queryFn: () =>
      apiClient
        .get<Template[]>('/templates', { params: { documentTypeId } })
        .then((r) => r.data),
    enabled: !!documentTypeId,
  });
}

export function useCreateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { documentTypeId: string; name: string; content: string }) =>
      apiClient.post<Template>('/templates', payload).then((r) => r.data),
    onSuccess: (t) => qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] }),
  });
}

export function useDeleteTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, documentTypeId }: { id: string; documentTypeId: string }) =>
      apiClient.delete(`/templates/${id}`).then(() => documentTypeId),
    onSuccess: (documentTypeId) => qc.invalidateQueries({ queryKey: ['templates', documentTypeId] }),
  });
}

export function useUpdateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, content }: { id: string; content: string }) =>
      apiClient.put<Template>(`/templates/${id}`, { content }).then((r) => r.data),
    onSuccess: (t) => qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] }),
  });
}

export function useUpdateTemplateSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (p: {
      id: string; documentTypeId: string;
      pageSize: string; pageOrientation: string;
      marginTop: number; marginRight: number; marginBottom: number; marginLeft: number;
    }) =>
      apiClient.put<Template>(`/templates/${p.id}/settings`, {
        pageSize: p.pageSize, pageOrientation: p.pageOrientation,
        marginTop: p.marginTop, marginRight: p.marginRight,
        marginBottom: p.marginBottom, marginLeft: p.marginLeft,
      }).then((r) => r.data),
    onSuccess: (t) => qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] }),
  });
}

export function useSetTemplateDefault() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string; documentTypeId: string }) =>
      apiClient.put<Template>(`/templates/${id}/set-default`).then((r) => r.data),
    onSuccess: (t) => qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] }),
  });
}
