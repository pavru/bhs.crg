import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentType, DocumentTypeKind } from './types';

export function useListDocumentTypes(kind?: DocumentTypeKind) {
  return useQuery({
    queryKey: ['document-types', kind ?? 'all'],
    queryFn: () =>
      apiClient
        .get<DocumentType[]>('/document-types', { params: kind ? { kind } : undefined })
        .then(r => r.data),
  });
}

export function useCreateDocumentType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: {
      name: string;
      code: string;
      kind: DocumentTypeKind;
      parentId?: string | null;
      schema: string;
      isAbstract?: boolean;
    }) => apiClient.post<DocumentType>('/document-types', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export function useUpdateDocumentType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name, code, parentId }: { id: string; name: string; code: string; parentId: string | null }) =>
      apiClient.put<DocumentType>(`/document-types/${id}`, { name, code, parentId }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export function useSetDocumentTypeAbstract() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, isAbstract }: { id: string; isAbstract: boolean }) =>
      apiClient.put<DocumentType>(`/document-types/${id}/abstract`, { isAbstract }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export function useDeleteDocumentType() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/document-types/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export function useUpdateDocumentTypeSchema() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, schema }: { id: string; schema: string }) =>
      apiClient.put<DocumentType>(`/document-types/${id}/schema`, { schema }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}
