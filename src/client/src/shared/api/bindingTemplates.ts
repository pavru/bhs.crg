import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DataSetBindingTemplate, RowFilterDef, ComputedColumn } from './types';

const base = (docTypeId: string) => `/document-types/${docTypeId}/binding-templates`;

export function useListBindingTemplates(documentTypeId: string | undefined) {
  return useQuery<DataSetBindingTemplate[]>({
    queryKey: ['binding-templates', documentTypeId],
    queryFn: () => apiClient.get(base(documentTypeId!)).then(r => r.data),
    enabled: !!documentTypeId,
  });
}

export function useCreateBindingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetBindingTemplate, Error, {
    documentTypeId: string;
    name: string;
    targetFieldKey?: string | null;
    columnMappings: Record<string, string>;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
  }>({
    mutationFn: ({ documentTypeId, ...body }) =>
      apiClient.post(base(documentTypeId), body).then(r => r.data),
    onSuccess: (_, { documentTypeId }) => {
      qc.invalidateQueries({ queryKey: ['binding-templates', documentTypeId] });
    },
  });
}

export function useUpdateBindingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetBindingTemplate, Error, {
    documentTypeId: string;
    id: string;
    name: string;
    targetFieldKey?: string | null;
    columnMappings: Record<string, string>;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
    sortOrder?: number;
  }>({
    mutationFn: ({ documentTypeId, id, ...body }) =>
      apiClient.put(`${base(documentTypeId)}/${id}`, body).then(r => r.data),
    onSuccess: (_, { documentTypeId }) => {
      qc.invalidateQueries({ queryKey: ['binding-templates', documentTypeId] });
    },
  });
}

export function useDeleteBindingTemplate() {
  const qc = useQueryClient();
  return useMutation<void, Error, { documentTypeId: string; id: string }>({
    mutationFn: ({ documentTypeId, id }) =>
      apiClient.delete(`${base(documentTypeId)}/${id}`).then(() => undefined),
    onSuccess: (_, { documentTypeId }) => {
      qc.invalidateQueries({ queryKey: ['binding-templates', documentTypeId] });
    },
  });
}
