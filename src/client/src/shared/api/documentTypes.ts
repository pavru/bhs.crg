import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentType, DocumentTypeKind } from './types';
import type { TypstRender } from './schema';

/** Проблема сборки Typst-блоков (issue #309, фаза 2). Глобальна: `typeId` — где живёт блок. */
export interface TypstBlockProblem {
  severity: 'error' | 'warning';
  code: 'cycle' | 'duplicate-fn' | 'syntax' | 'checker-unavailable';
  message: string;
  typeId: string | null;
  typeName: string | null;
  variantName: string | null;
  fnName: string | null;
  line: number | null;
}

/** Проверка сборки всех Typst-блоков с draft-overlay текущего типа (тело = его черновик renders). */
export function useValidateTypstBlocks() {
  return useMutation({
    mutationFn: ({ typeId, renders }: { typeId: string; renders: TypstRender[] }) =>
      apiClient.post<TypstBlockProblem[]>(`/document-types/${typeId}/validate-typst-blocks`, renders).then(r => r.data),
  });
}

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

export function useSetDocumentTypeAllowsProxy() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, allowsProxy }: { id: string; allowsProxy: boolean }) =>
      apiClient.put<DocumentType>(`/document-types/${id}/allows-proxy`, { allowsProxy }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export function useSetDocumentTypeGroup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, group }: { id: string; group: string | null }) =>
      apiClient.put<DocumentType>(`/document-types/${id}/group`, { group }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['document-types'] }),
  });
}

export interface DocumentTypeUsageReason { kind: string; label: string; count: number; names: string[] }
export interface DocumentTypeUsage { reasons: DocumentTypeUsageReason[]; inUse: boolean }

/** Использование типа документа (issue #275) — проактивно, почему тип нельзя удалить.
 *  Тот же набор проверок, что и guard удаления; `id` пуст → запрос отключён. */
export function useDocumentTypeUsage(id: string | undefined) {
  return useQuery({
    queryKey: ['document-type-usage', id],
    enabled: !!id,
    queryFn: () => apiClient.get<DocumentTypeUsage>(`/document-types/${id}/usage`).then(r => r.data),
  });
}

export interface AuditFinding { instanceId: string; instanceName: string; code: string; severity: string; path: string; message: string }
export interface DocumentTypeAuditReport { typeId: string; typeName: string; instanceCount: number; findings: AuditFinding[] }

/** Аудит типа (issue #348): расхождения данных существующих инстансов с текущей схемой. По требованию (enabled). */
export function useAuditDocumentType(id: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ['document-type-audit', id],
    enabled: !!id && enabled,
    staleTime: 0,
    queryFn: () => apiClient.get<DocumentTypeAuditReport>(`/document-types/${id}/audit`).then(r => r.data),
  });
}

export interface AuditFix { instanceId: string; action: 'remove' | 'rename'; path: string; targetKey?: string }
export interface AuditFixOutcome { instanceId: string; path: string; action: string; applied: boolean; reason?: string; oldValue?: string }
export interface ApplyAuditFixesResult { applied: number; skipped: number; outcomes: AuditFixOutcome[] }

/** Применение исправлений аудита (issue #350) — мутирует данные инстансов; инвалидирует отчёт и наборы. */
export function useApplyAuditFixes(typeId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (fixes: AuditFix[]) =>
      apiClient.post<ApplyAuditFixesResult>(`/document-types/${typeId}/audit/apply`, { fixes }).then(r => r.data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['document-type-audit', typeId] });
      qc.invalidateQueries({ queryKey: ['document-sets'] });
    },
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
