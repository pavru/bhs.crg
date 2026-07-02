import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogScope } from './types';

export type QualityDocSource = 'Manual' | 'Fgis' | 'Manufacturer' | 'Web';

export interface QualityDocument {
  id: string;
  documentTypeId: string;
  displayName: string;
  requisites: Record<string, unknown>;
  scanBlobPath?: string | null;
  scanFileName?: string | null;
  scanMimeType?: string | null;
  source: QualityDocSource;
  scope: CatalogScope;
  scopeId?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MaterialQualityLink {
  id: string;
  scope: CatalogScope;
  scopeId?: string | null;
  materialKey: string;
  qualityDocumentId: string;
}

// ─── Library ────────────────────────────────────────────────────────────────

export function useListQualityDocs(params: { scope?: CatalogScope; scopeId?: string; search?: string; enabled?: boolean }) {
  const { scope, scopeId, search, enabled = true } = params;
  return useQuery({
    queryKey: ['quality-docs', scope ?? null, scopeId ?? null, search ?? ''],
    queryFn: () => apiClient.get<QualityDocument[]>('/quality-docs', { params: { scope, scopeId, search } }).then(r => r.data),
    enabled,
  });
}

export function useCreateQualityDoc() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: {
      documentTypeId: string; displayName: string; requisites: Record<string, unknown>;
      scope: CatalogScope; scopeId?: string | null; source?: QualityDocSource;
      scanBlobPath?: string | null; scanFileName?: string | null; scanMimeType?: string | null;
    }) => apiClient.post<QualityDocument>('/quality-docs', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quality-docs'] }),
  });
}

export function useUpdateQualityDoc() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, documentTypeId, displayName, requisites }: { id: string; documentTypeId: string; displayName: string; requisites: Record<string, unknown> }) =>
      apiClient.put<QualityDocument>(`/quality-docs/${id}`, { documentTypeId, displayName, requisites }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quality-docs'] }),
  });
}

export function useSetQualityDocScan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, scanBlobPath, scanFileName, scanMimeType }: { id: string; scanBlobPath?: string | null; scanFileName?: string | null; scanMimeType?: string | null }) =>
      apiClient.put<QualityDocument>(`/quality-docs/${id}/scan`, { scanBlobPath, scanFileName, scanMimeType }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quality-docs'] }),
  });
}

export function useDeleteQualityDoc() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/quality-docs/${id}`).then(() => id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['quality-docs'] });
      qc.invalidateQueries({ queryKey: ['quality-doc-links'] });
    },
  });
}

// ─── Recognition (vision-LLM) ─────────────────────────────────────────────────

export interface RecognitionFieldReq { path: string; title: string; type: string; options?: string[]; }

/** Извлекает реквизиты из загруженного скана (blobPath) по списку плоских полей.
 * promptKind: 'titleblock' — промпт под штамп чертежа/документа по ГОСТ Р 21.101-2020,
 * не задан — общий промпт (сертификат/декларация). */
export async function recognizeDocument(
  req: { blobPath: string; mimeType: string; fields: RecognitionFieldReq[]; silent?: boolean; promptKind?: 'titleblock' },
): Promise<{ values: Record<string, string>; pageCount: number | null }> {
  const { data } = await apiClient.post<{ values: Record<string, string>; pageCount: number | null }>('/quality-docs/recognize', req);
  return { values: data.values ?? {}, pageCount: data.pageCount ?? null };
}

// ─── Web search & import ──────────────────────────────────────────────────────

export interface SearchCandidate { title: string; url: string; snippet: string; source: string; }

export async function searchQualityDocs(query: string): Promise<SearchCandidate[]> {
  const { data } = await apiClient.post<SearchCandidate[]>('/quality-docs/search', { query });
  return data;
}

export async function importQualityDocFromUrl(req: { url: string; title: string; documentTypeId: string; scope: CatalogScope; scopeId?: string | null }): Promise<QualityDocument> {
  const { data } = await apiClient.post<QualityDocument>('/quality-docs/import-url', req);
  return data;
}

// ─── Suggestions ──────────────────────────────────────────────────────────────

export interface LinkSuggestion {
  materialKey: string;
  materialName: string;
  qualityDocumentId: string;
  docDisplayName: string;
  score: number;
}

export async function suggestLinks(req: { setId: string; materials: { key: string; name: string }[] }): Promise<LinkSuggestion[]> {
  const { data } = await apiClient.post<LinkSuggestion[]>('/quality-docs/suggest', req);
  return data;
}

// ─── Links ──────────────────────────────────────────────────────────────────

export function useListMaterialLinks(params: { scope: CatalogScope; scopeId?: string; enabled?: boolean }) {
  const { scope, scopeId, enabled = true } = params;
  return useQuery({
    queryKey: ['quality-doc-links', scope, scopeId ?? null],
    queryFn: () => apiClient.get<MaterialQualityLink[]>('/quality-docs/links', { params: { scope, scopeId } }).then(r => r.data),
    enabled,
  });
}

export function useSetMaterialLinks() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { scope: CatalogScope; scopeId?: string | null; materialKeys: string[]; qualityDocumentId: string }) =>
      apiClient.post<{ linked: number }>('/quality-docs/links', payload).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quality-doc-links'] }),
  });
}

export function useRemoveMaterialLink() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/quality-docs/links/${id}`).then(() => id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['quality-doc-links'] }),
  });
}
