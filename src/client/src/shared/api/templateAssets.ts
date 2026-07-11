import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

export type TemplateAssetScope = 'Template' | 'DocumentType' | 'System';
export type TemplateAssetKind = 'Image' | 'Font';

export interface TemplateAssetDto {
  id: string;
  scope: TemplateAssetScope;
  scopeId: string | null;
  kind: TemplateAssetKind;
  name: string;
  fileName: string;
  mimeType: string;
  fontFamilyName: string | null;
  createdAt: string;
  updatedAt: string;
}

const QK = 'templateAssets';

export function useListTemplateAssets(scope: TemplateAssetScope, scopeId: string | null) {
  return useQuery<TemplateAssetDto[]>({
    queryKey: [QK, scope, scopeId],
    queryFn: () => apiClient.get('/template-assets', { params: { scope, scopeId } }).then(r => r.data),
  });
}

export function useUploadTemplateAsset() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ file, scope, scopeId, name }: {
      file: File; scope: TemplateAssetScope; scopeId: string | null; name: string;
    }) => {
      const formData = new FormData();
      formData.append('file', file);
      return apiClient.post<TemplateAssetDto>('/template-assets', formData, {
        params: { scope, scopeId, name },
      }).then(r => r.data);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useReplaceTemplateAsset() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, file }: { id: string; file: File }) => {
      const formData = new FormData();
      formData.append('file', file);
      return apiClient.put<TemplateAssetDto>(`/template-assets/${id}`, formData).then(r => r.data);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useDeleteTemplateAsset() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/template-assets/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}
