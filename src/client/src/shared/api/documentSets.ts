import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentSet, DocumentInstance } from './types';

export function useGetAvailableInstances(setId: string | undefined) {
  return useQuery({
    queryKey: ['document-sets', setId, 'available-instances'],
    queryFn: () =>
      apiClient.get<DocumentInstance[]>(`/document-sets/${setId}/available-instances`).then(r => r.data),
    enabled: !!setId,
  });
}

export function useGetDocumentSet(id: string | undefined) {
  return useQuery({
    queryKey: ['document-sets', id],
    queryFn: () => apiClient.get<DocumentSet>(`/document-sets/${id}`).then((r) => r.data),
    enabled: !!id,
  });
}

export function useAddDocumentToSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, documentTypeId }: { setId: string; documentTypeId: string }) =>
      apiClient
        .post<DocumentInstance>(`/document-sets/${setId}/documents`, { documentTypeId })
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useRenameDocumentInstance() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, name }: { setId: string; instanceId: string; name: string }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/name`, { name })
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useDeleteDocumentInstance() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId }: { setId: string; instanceId: string }) =>
      apiClient.delete(`/document-sets/${setId}/documents/${instanceId}`).then(() => setId),
    onSuccess: (_setId, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useUpdateRequisites() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, requisites }: { setId: string; instanceId: string; requisites: Record<string, unknown> }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/requisites`, requisites)
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useUpdateEntityRefs() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, entityRefs }: { setId: string; instanceId: string; entityRefs: Record<string, unknown> }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/entity-refs`, entityRefs)
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useSetDocumentTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, templateId }: { setId: string; instanceId: string; templateId: string | null }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/template`, { templateId })
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export function useGenerateDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ instanceId, format }: { instanceId: string; setId: string; format: 'Pdf' | 'Docx' }) =>
      apiClient
        .post<{ id: string; blobPath: string; format: string }>(`/generate/${instanceId}`, { format })
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export async function downloadGeneratedFile(instanceId: string, format: 'pdf' | 'docx') {
  const response = await apiClient.get(`/generate/download/${instanceId}/${format}`, {
    responseType: 'blob',
  });
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `document.${format}`;
  a.click();
  URL.revokeObjectURL(url);
}

export interface ResolutionDiagnostic {
  severity: 'error' | 'warning';
  path: string;
  message: string;
}

/** Проверяет разрешение ссылок экземпляра «по требованию» (warning/error). */
export async function validateResolution(instanceId: string): Promise<ResolutionDiagnostic[]> {
  const r = await apiClient.get<ResolutionDiagnostic[]>(`/generate/validate/${instanceId}`);
  return r.data;
}

/** Скачивает ZIP (template.typ + data.json + typeblocks.typ + userlib.typ) для отладки шаблона. */
export async function downloadDebugBundle(instanceId: string) {
  const response = await apiClient.get(`/generate/debug-bundle/${instanceId}`, {
    responseType: 'blob',
  });
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `typst-debug-${instanceId}.zip`;
  a.click();
  URL.revokeObjectURL(url);
}

export async function previewGeneratedFile(instanceId: string, format: 'pdf' | 'docx') {
  // Open window synchronously (in user-gesture context) to avoid popup blocker,
  // then navigate it to the blob URL once the download completes.
  const newWindow = window.open('', '_blank');
  if (!newWindow) return;

  try {
    const response = await apiClient.get(`/generate/download/${instanceId}/${format}`, {
      responseType: 'blob',
    });
    const url = URL.createObjectURL(response.data as Blob);
    newWindow.location.href = url;
    // intentionally not revoking — browser needs the URL while the tab is open
  } catch {
    newWindow.close();
  }
}
