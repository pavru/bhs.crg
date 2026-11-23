import { useQuery, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentType, Template, TemplateSummary } from './types';

export interface TemplateType extends DocumentType {
  printRequisites?: {
    fields: { path: string[]; title: string; type: string }[];
    customFieldsKey?: string | null;
    excludedCustomKeys?: string[] | null;
  } | null;
}

export function useTemplateTypes() {
  return useQuery({
    queryKey: ['template-types'],
    staleTime: 0,
    queryFn: () => apiClient.get<{ type: DocumentType; printRequisites?: TemplateType['printRequisites'] }[]>('/templates/types')
      .then(r => r.data.map(({ type, printRequisites }): TemplateType => ({ ...type, printRequisites }))),
  });
}

/** Версии шаблонов типа — без текстов (issue #1972). Текст версии — `useTemplateContent`. */
export function useListTemplates(documentTypeId: string | undefined) {
  return useQuery({
    queryKey: ['templates', documentTypeId],
    queryFn: () =>
      apiClient
        .get<TemplateSummary[]>('/templates', { params: { documentTypeId } })
        .then((r) => r.data),
    enabled: !!documentTypeId,
  });
}

export const templateContentKey = (id: string) => ['template-content', id] as const;

/** Текст Typst одной версии (issue #1972) — его читает редактор, а не список. */
export function useTemplateContent(id: string) {
  return useQuery({
    queryKey: templateContentKey(id),
    queryFn: () =>
      apiClient.get<{ content: string }>(`/templates/${id}/content`).then((r) => r.data.content),
  });
}

/**
 * Записанная версия: ответ записи несёт её текст целиком — он кладётся в кэш текста, чтобы редактор не
 * перечитывал только что сохранённое и не мигал загрузкой на новой версии; список типа перечитывается.
 * Запрос текста, начатый ДО записи (перечитывание по фокусу окна), отменяется: иначе он пришёл бы
 * позже и подменил сохранённый текст прежним.
 */
async function templateWritten(qc: QueryClient, t: Template) {
  await qc.cancelQueries({ queryKey: templateContentKey(t.id), exact: true });
  qc.setQueryData(templateContentKey(t.id), t.content);
  void qc.invalidateQueries({ queryKey: ['templates', t.documentTypeId] });
}

export function useCreateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { documentTypeId: string; name: string; content: string }) =>
      apiClient.post<Template>('/templates', payload).then((r) => r.data),
    onSuccess: (t) => templateWritten(qc, t),
  });
}

export function useDuplicateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; documentTypeId: string; name?: string }) =>
      apiClient.post<Template>(`/templates/${id}/duplicate`, { name }).then((r) => r.data),
    onSuccess: (t) => templateWritten(qc, t),
  });
}

/** Использование версии шаблона (issue #364): сколько документов запиннуто + примеры имён. */
export interface TemplateUsage { count: number; names: string[] }

/** Карта использования версий типа (templateId → usage). Версии без пинов отсутствуют (= 0). */
export function useTemplatesUsage(documentTypeId: string | undefined) {
  return useQuery({
    queryKey: ['template-usage', documentTypeId],
    enabled: !!documentTypeId,
    queryFn: () =>
      apiClient
        .get<Record<string, TemplateUsage>>('/templates/usage', { params: { documentTypeId } })
        .then((r) => r.data),
  });
}

export function useDeleteTemplate() {
  const qc = useQueryClient();
  return useMutation({
    // reassign=true — снять пин у документов (→ дефолт) + сброс в Draft; иначе бэк откажет (409), если версия используется.
    mutationFn: ({ id, documentTypeId, reassign }: { id: string; documentTypeId: string; reassign?: boolean }) =>
      apiClient.delete(`/templates/${id}`, { params: reassign ? { reassign: true } : undefined }).then(() => documentTypeId),
    onSuccess: (documentTypeId) => {
      qc.invalidateQueries({ queryKey: ['templates', documentTypeId] });
      qc.invalidateQueries({ queryKey: ['template-usage', documentTypeId] });
    },
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
    onSuccess: (res) => templateWritten(qc, res.template),
  });
}

/** Явное «Сохранить как новую версию» (issue #360) — форк новой версии + опц. примечание. */
export function useCreateTemplateVersion() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, content, comment }: { id: string; content: string; comment?: string | null }) =>
      apiClient.post<TemplateMutationResult>(`/templates/${id}/versions`, { content, comment }).then((r) => r.data),
    onSuccess: (res) => templateWritten(qc, res.template),
  });
}

/** Объявление параметров шаблона — parameters = JSON-текст TemplateParam[] или null. */
export function useUpdateTemplateParameters() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (p: { id: string; parameters: string | null }) =>
      apiClient.put<Template>(`/templates/${p.id}/parameters`, { parameters: p.parameters }).then((r) => r.data),
    onSuccess: (t) => templateWritten(qc, t),
  });
}

export function useSetTemplateDefault() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: string; documentTypeId: string }) =>
      apiClient.put<TemplateMutationResult>(`/templates/${id}/set-default`).then((r) => r.data),
    onSuccess: (res) => templateWritten(qc, res.template),
  });
}
