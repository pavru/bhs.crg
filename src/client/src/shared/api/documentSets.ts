import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { DocumentSet, DocumentInstance, DocumentSearchResult } from './types';
import { filenameFromContentDisposition } from './attachments';

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

/** Предупреждение о воздействии копирования/переноса на ссылки (issue #283). */
export interface CopyWarning { kind: string; label: string; count: number; names: string[] }

/** Dry-run: что затронет копирование в целевой комплект — для превью в диалоге ДО подтверждения. */
export function usePreviewCopyDocument(setId: string, instanceId: string | undefined, targetSetId: string | undefined) {
  return useQuery({
    queryKey: ['copy-preview', instanceId, targetSetId],
    enabled: !!instanceId && !!targetSetId,
    queryFn: () =>
      apiClient
        .post<CopyWarning[]>(`/document-sets/${setId}/documents/${instanceId}/copy/preview`, { targetSetId })
        .then((r) => r.data),
  });
}

export function useCopyDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, targetSetId }: { setId: string; instanceId: string; targetSetId: string }) =>
      apiClient
        .post<{ instance: DocumentInstance; warnings: CopyWarning[] }>(
          `/document-sets/${setId}/documents/${instanceId}/copy`, { targetSetId })
        .then((r) => r.data),
    onSuccess: (_d, { targetSetId }) => qc.invalidateQueries({ queryKey: ['document-sets', targetSetId] }),
  });
}

/** Превью переноса: затронутые ссылки + имена объектов, из-за которых перенос заблокирован (#283). */
export interface MovePreview { warnings: CopyWarning[]; blockedBy: string[] }

export function usePreviewMoveDocument(setId: string, instanceId: string | undefined, targetSetId: string | undefined) {
  return useQuery({
    queryKey: ['move-preview', instanceId, targetSetId],
    enabled: !!instanceId && !!targetSetId,
    queryFn: () =>
      apiClient
        .post<MovePreview>(`/document-sets/${setId}/documents/${instanceId}/move/preview`, { targetSetId })
        .then((r) => r.data),
  });
}

export function useMoveDocument() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId, targetSetId }: { setId: string; instanceId: string; targetSetId: string }) =>
      apiClient
        .post<{ instance: DocumentInstance; warnings: CopyWarning[] }>(
          `/document-sets/${setId}/documents/${instanceId}/move`, { targetSetId })
        .then((r) => r.data),
    onSuccess: (_d, { setId, targetSetId }) => {
      qc.invalidateQueries({ queryKey: ['document-sets', setId] });
      qc.invalidateQueries({ queryKey: ['document-sets', targetSetId] });
    },
  });
}

export function useDuplicateDocumentInstance() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ setId, instanceId }: { setId: string; instanceId: string }) =>
      apiClient
        .post<DocumentInstance>(`/document-sets/${setId}/documents/${instanceId}/duplicate`)
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
    onSuccess: (_d, { setId, instanceId }) => {
      qc.invalidateQueries({ queryKey: ['document-sets', setId] });
      // Реквизиты изменились → диагностика битых ссылок могла устареть (issue #332/#334): перепроверить.
      qc.invalidateQueries({ queryKey: ['resolution-diagnostics', instanceId] });
    },
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

/** Отправка собранного комплекта на заданные адреса (подписчики + произвольные), фоновая задача. */
export function useEmailSet() {
  return useMutation({
    mutationFn: ({ setId, to, subject, body }: { setId: string; to: string[]; subject?: string; body?: string }) =>
      apiClient.post<{ jobId: string }>(`/document-sets/${setId}/email`, { to, subject, body }).then(r => r.data),
  });
}

/** Отправка отдельного документа (его сгенерированных PDF) на заданные адреса, фоновая задача. */
export function useEmailDocument() {
  return useMutation({
    mutationFn: ({ setId, instanceId, to, subject, body }: { setId: string; instanceId: string; to: string[]; subject?: string; body?: string }) =>
      apiClient.post<{ jobId: string }>(`/document-sets/${setId}/documents/${instanceId}/email`, { to, subject, body }).then(r => r.data),
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
  // Имя формирует бэкенд (имя документа + суффикс-шаблон, спецсимволы → _), достаём из Content-Disposition.
  const filename = filenameFromContentDisposition(response.headers['content-disposition'], 'document.pdf');
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export interface ResolutionDiagnostic {
  severity: 'error' | 'warning';
  path: string;
  message: string;
  /** Вид проблемы (issue #332): "leftover-ref" — висячая ссылка (цель удалена), "missing-required" — пустое обязательное. */
  code?: string;
}

/**
 * Общий кэш диагностики разрешения ссылок экземпляра (issue #332). Один фетч на instanceId —
 * его читают И панель «Проверить ссылки», И индикаторы битых ссылок на полях (без второго запроса
 * и расхождений). Автозапуск фоном при `enabled` (напр. когда в реквизитах есть ref-поля).
 */
export function useResolutionDiagnostics(instanceId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: ['resolution-diagnostics', instanceId],
    queryFn: () => validateResolution(instanceId!),
    enabled: !!instanceId && enabled,
    staleTime: 30_000,
  });
}

/** Пути битых ссылок (leftover-ref) из диагностики — для пометки полей. */
export function brokenRefPaths(diagnostics: ResolutionDiagnostic[] | undefined): Set<string> {
  return new Set((diagnostics ?? []).filter(d => d.code === 'leftover-ref').map(d => d.path));
}

export type PreviewResult =
  | { kind: 'pdf'; url: string }
  | { kind: 'no-template' }
  | { kind: 'error'; message: string };

/**
 * Живой предпросмотр документа (issue #193): POST текущих (несохранённых) реквизитов →
 * эфемерный PDF по дефолтному шаблону. Успех → blob-URL PDF; нет шаблона / ошибка → маркер.
 * Ответ приходит как blob (и PDF, и JSON-статус) — различаем по content-type.
 */
export async function previewDocument(instanceId: string, requisites: unknown): Promise<PreviewResult> {
  async function parseJsonBlob(b: Blob): Promise<PreviewResult> {
    try {
      const j = JSON.parse(await b.text());
      if (j.noTemplate) return { kind: 'no-template' };
      return { kind: 'error', message: j.error ?? 'Ошибка предпросмотра' };
    } catch { return { kind: 'error', message: 'Ошибка предпросмотра' }; }
  }
  try {
    const res = await apiClient.post(`/generate/preview/${instanceId}`, requisites, { responseType: 'blob' });
    const ct = String(res.headers['content-type'] ?? '');
    if (ct.includes('application/pdf')) return { kind: 'pdf', url: URL.createObjectURL(res.data as Blob) };
    return await parseJsonBlob(res.data as Blob);
  } catch (e: unknown) {
    const data = (e as { response?: { data?: unknown } })?.response?.data;
    if (data instanceof Blob) return await parseJsonBlob(data);
    return { kind: 'error', message: e instanceof Error ? e.message : 'Ошибка предпросмотра' };
  }
}

/** Аудит одного документа (issue #352): расхождения его данных с текущей схемой. По требованию. */
export function useAuditInstance(setId: string, instanceId: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ['instance-audit', instanceId],
    enabled: !!instanceId && enabled,
    staleTime: 0,
    queryFn: () => apiClient
      .get<import('./documentTypes').AuditFinding[]>(`/document-sets/${setId}/documents/${instanceId}/audit`)
      .then(r => r.data),
  });
}

/** Применение исправлений к ЭТОМУ документу (issue #352) — юзер лечит свой документ без админа. */
export function useApplyInstanceAuditFixes(setId: string, instanceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (fixes: { action: 'remove' | 'rename'; path: string; targetKey?: string }[]) =>
      apiClient.post<import('./documentTypes').ApplyAuditFixesResult>(
        `/document-sets/${setId}/documents/${instanceId}/audit/apply`, { fixes }).then(r => r.data),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['instance-audit', instanceId] });
      qc.invalidateQueries({ queryKey: ['document-sets', setId] });
    },
  });
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
  // Плейсхолдер, пока грузится (и чтобы при ошибке не осталась «пустая страница»).
  try { newWindow.document.write('<p style="font:14px sans-serif;color:#555;padding:16px">Загрузка PDF…</p>'); } catch { /* cross-origin — не критично */ }

  try {
    const path = templateId ? `/generate/download/${instanceId}/${templateId}/pdf` : `/generate/download/${instanceId}/pdf`;
    const response = await apiClient.get(path, {
      responseType: 'blob',
    });
    const url = URL.createObjectURL(response.data as Blob);
    newWindow.location.href = url;
    // intentionally not revoking — browser needs the URL while the tab is open
  } catch {
    // Не оставляем пустую вкладку — показываем понятное сообщение.
    try {
      newWindow.document.body.innerHTML =
        '<p style="font:14px sans-serif;color:#b00;padding:16px">Не удалось открыть файл. Обновите страницу документа и попробуйте снова.</p>';
    } catch { newWindow.close(); }
  }
}
