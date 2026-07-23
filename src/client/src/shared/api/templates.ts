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

export function useDuplicateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; documentTypeId: string; name?: string }) =>
      apiClient.post<Template>(`/templates/${id}/duplicate`, { name }).then((r) => r.data),
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

/** Результат мутации шаблона, влияющей на вывод (issue #362): шаблон + число документов,
 *  сброшенных в Draft из-за устаревшего PDF. */
export interface TemplateMutationResult {
  template: Template;
  resetDocuments: number;
}

/** Простое сохранение (issue #360, Ctrl+S) — правит содержимое активной версии на месте, без новой версии. */
export function useSaveTemplateContent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, content }: { id: string; content: string }) =>
      apiClient.put<TemplateMutationResult>(`/templates/${id}/content`, { content }).then((r) => r.data),
    onSuccess: (res) => qc.invalidateQueries({ queryKey: ['templates', res.template.documentTypeId] }),
  });
}

/** Явное «Сохранить как новую версию» (issue #360) — форк новой версии + опц. примечание. */
export function useCreateTemplateVersion() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, content, comment }: { id: string; content: string; comment?: string | null }) =>
      apiClient.post<TemplateMutationResult>(`/templates/${id}/versions`, { content, comment }).then((r) => r.data),
    onSuccess: (res) => qc.invalidateQueries({ queryKey: ['templates', res.template.documentTypeId] }),
  });
}

/** Объявление параметров шаблона — parameters = JSON-текст TemplateParam[] или null. */
export function useUpdateTemplateParameters() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (p: { id: string; parameters: string | null }) =>
      apiClient.put<Template>(`/templates/${p.id}/parameters`, { parameters: p.parameters }).then((r) => r.data),
    onSuccess: (t) => qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] }),
  });
}

export function useSetTemplateDefault() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string; documentTypeId: string }) =>
      apiClient.put<TemplateMutationResult>(`/templates/${id}/set-default`).then((r) => r.data),
    onSuccess: (res) => qc.invalidateQueries({ queryKey: ['templates', res.template.documentTypeId] }),
  });
}
