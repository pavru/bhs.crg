import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentSet, DocumentInstance, DocumentSearchResult } from './types';

/** Поиск документов по всем комплектам (имя документа/типа + текст реквизитов). Пустой q → без запроса. */
export function useSearchDocuments(q: string, constructionId?: string) {
  return useQuery({
    queryKey: ['document-search', q.trim(), constructionId ?? null],
    queryFn: () =>
      apiClient.get<DocumentSearchResult[]>('/document-sets/search', {
        params: { q: q.trim(), ...(constructionId ? { constructionId } : {}) },
      }).then(r => r.data),
    enabled: q.trim().length > 0,
  });
}

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

/** Набор выбранных шаблонов для мульти-генерации — templateIds = массив id (пусто → один дефолт). */
export function useSetDocumentTemplates() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, templateIds }: { setId: string; instanceId: string; templateIds: string[] }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/templates`, templateIds.length ? templateIds : null)
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

/** Переопределения значений параметров шаблона на документе — params = объект {имя:значение} или null. */
export function useSetDocumentTemplateParams() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, params }: { setId: string; instanceId: string; params: Record<string, unknown> | null }) =>
      apiClient
        .put<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/template-params`, params)
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

/** Задаёт порядок документов в комплекте (для сборки) — orderedIds в нужном порядке. */
export function useReorderInstances() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, orderedIds }: { setId: string; orderedIds: string[] }) =>
      apiClient.put<DocumentSet>(`/document-sets/${setId}/documents/order`, orderedIds).then(r => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

/** Отправка собранного комплекта подписчикам (фоновая задача). */
export function useEmailSetToSubscribers() {
  return useMutation({
    mutationFn: ({ setId, subject, body }: { setId: string; subject?: string; body?: string }) =>
      apiClient.post<{ jobId: string }>(`/document-sets/${setId}/email-to-subscribers`, { subject, body }).then(r => r.data),
  });
}

/** Запускает сборку комплекта в один PDF (фоновая задача). instanceIds — подмножество или пусто (весь). */
export function useAssembleSet() {
  return useMutation({
    mutationFn: ({ setId, instanceIds }: { setId: string; instanceIds?: string[] }) =>
      apiClient.post<{ jobId: string }>(`/document-sets/${setId}/assemble`,
        { instanceIds: instanceIds && instanceIds.length ? instanceIds : null }).then(r => r.data),
  });
}

export interface DocumentSetOutputInfo { generatedAt: string; format: string; }

/** Метаданные собранного комплекта (null, если ещё не собран). refetchInterval — для слежения во время сборки. */
export function useDocumentSetOutput(setId: string | undefined, refetchInterval: number | false = false) {
  return useQuery({
    queryKey: ['document-sets', setId, 'output'],
    queryFn: () =>
      apiClient.get<DocumentSetOutputInfo>(`/document-sets/${setId}/output`)
        .then(r => r.data)
        .catch(err => { if (err?.response?.status === 404) return null; throw err; }),
    enabled: !!setId,
    refetchInterval,
  });
}

export async function downloadSetOutput(setId: string, fallbackName = 'Комплект') {
  const response = await apiClient.get(`/document-sets/${setId}/output/download`, { responseType: 'blob' });
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `${fallbackName}.pdf`;
  a.click();
  URL.revokeObjectURL(url);
}

export function useGenerateDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ instanceId }: { instanceId: string; setId: string }) =>
      apiClient
        .post<{ id: string; blobPath: string; format: string }>(`/generate/${instanceId}`, { format: 'Pdf' })
        .then((r) => r.data),
    onSuccess: (_d, { setId }) => qc.invalidateQueries({ queryKey: ['document-sets', setId] }),
  });
}

export async function downloadGeneratedFile(instanceId: string, templateId?: string | null) {
  const path = templateId ? `/generate/download/${instanceId}/${templateId}/pdf` : `/generate/download/${instanceId}/pdf`;
  const response = await apiClient.get(path, {
    responseType: 'blob',
  });
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'document.pdf';
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

export async function previewGeneratedFile(instanceId: string, templateId?: string | null) {
  // Open window synchronously (in user-gesture context) to avoid popup blocker,
  // then navigate it to the blob URL once the download completes.
  const newWindow = window.open('', '_blank');
  if (!newWindow) return;

  try {
    const path = templateId ? `/generate/download/${instanceId}/${templateId}/pdf` : `/generate/download/${instanceId}/pdf`;
    const response = await apiClient.get(path, {
      responseType: 'blob',
    });
    const url = URL.createObjectURL(response.data as Blob);
    newWindow.location.href = url;
    // intentionally not revoking — browser needs the URL while the tab is open
  } catch {
    newWindow.close();
  }
}
