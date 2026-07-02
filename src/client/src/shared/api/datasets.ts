import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type {
  CatalogScope, ColumnExprDef, DataSetBinding, DataSetBindingPreviewResult, DataSetFile,
  DataSetPreview, DataSetProcessingTemplate, DataSetSource, RowFilterDef, ComputedColumn, SortSpec,
} from './types';

// ── Файлы ─────────────────────────────────────────────────────────────────────

export function useListDataSetFiles(scope: CatalogScope, scopeId?: string) {
  return useQuery<DataSetFile[]>({
    queryKey: ['datasets', 'files', scope, scopeId],
    queryFn: () =>
      apiClient.get('/datasets/files', { params: { scope, scopeId } }).then(r => r.data),
  });
}

export function useUploadDataSetFile() {
  const qc = useQueryClient();
  return useMutation<DataSetFile, Error, {
    file: File;
    name: string;
    scope: CatalogScope;
    scopeId?: string;
  }>({
    mutationFn: ({ file, name, scope, scopeId }) => {
      const form = new FormData();
      form.append('file', file);
      form.append('name', name);
      form.append('scope', scope);
      if (scopeId) form.append('scopeId', scopeId);
      return apiClient.post('/datasets/files', form).then(r => r.data);
    },
    onSuccess: (_, { scope, scopeId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'files', scope, scopeId] });
    },
  });
}

export function useUpdateDataSetFile() {
  const qc = useQueryClient();
  return useMutation<DataSetFile, Error, {
    id: string;
    file: File;
    name?: string;
    scope: CatalogScope;
    scopeId?: string;
  }>({
    mutationFn: ({ id, file, name }) => {
      const form = new FormData();
      form.append('file', file);
      if (name) form.append('name', name);
      return apiClient.put(`/datasets/files/${id}`, form).then(r => r.data);
    },
    onSuccess: (_, { scope, scopeId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'files', scope, scopeId] });
      qc.invalidateQueries({ queryKey: ['datasets', 'available'] });
    },
  });
}

export function useDeleteDataSetFile() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string; scope: CatalogScope; scopeId?: string }>({
    mutationFn: ({ id }) => apiClient.delete(`/datasets/files/${id}`).then(() => undefined),
    onSuccess: (_, { scope, scopeId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'files', scope, scopeId] });
    },
  });
}

// ── Источники (ручное управление — для XML) ────────────────────────────────────

export function useCreateDataSetSource() {
  const qc = useQueryClient();
  return useMutation<DataSetSource, Error, {
    fileId: string;
    name: string;
    sheetOrPath: string;
    columnExpressions?: ColumnExprDef[] | null;
  }>({
    mutationFn: ({ fileId, ...data }) =>
      apiClient.post(`/datasets/files/${fileId}/sources`, data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'files'] }),
  });
}

export function useUpdateDataSetSource() {
  const qc = useQueryClient();
  return useMutation<DataSetSource, Error, {
    id: string;
    name: string;
    sheetOrPath: string;
    columnExpressions?: ColumnExprDef[] | null;
  }>({
    mutationFn: ({ id, ...data }) =>
      apiClient.put(`/datasets/sources/${id}`, data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'files'] }),
  });
}

/** Пути XML-записей внутри ZIP-файла — для выбора при ручном создании источника. */
export function useListZipXmlEntries(fileId: string | undefined) {
  return useQuery<string[]>({
    queryKey: ['datasets', 'zip-xml-entries', fileId],
    queryFn: () => apiClient.get(`/datasets/files/${fileId}/zip-xml-entries`).then(r => r.data),
    enabled: !!fileId,
  });
}

export interface ExpressionPreviewSpec {
  fileId: string;
  /** Полный row-selector (с учётом "entry::" для ZIP) — контекст, либо сам предпросматриваемый путь. */
  rowSelector: string;
  /** Задан — предпросмотр значения колонки (относительно rowSelector); не задан — предпросмотр самого rowSelector. */
  expr?: string;
}

export interface ExpressionPreviewResult {
  matchCount: number;
  samples: string[];
}

/** Предпросмотр XPath/JSONPath-выражения в builder'е — без сохранения источника. */
export function useExpressionPreview(spec: ExpressionPreviewSpec | null) {
  return useQuery<ExpressionPreviewResult>({
    queryKey: ['datasets', 'expression-preview', spec?.fileId, spec?.rowSelector, spec?.expr],
    queryFn: () =>
      apiClient.post(`/datasets/files/${spec!.fileId}/expression-preview`,
        { rowSelector: spec!.rowSelector, expr: spec!.expr }).then(r => r.data),
    enabled: !!spec && !!spec.rowSelector.trim(),
    retry: false,
  });
}

export function useDeleteDataSetSource() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.delete(`/datasets/sources/${id}`).then(() => undefined),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'files'] }),
  });
}

// ── Обработка источника (Filter/Conversion/Sort) — лёгкая правка, файл не трогает ─────

export function useSetDataSetSourceProcessing() {
  const qc = useQueryClient();
  return useMutation<DataSetSource, Error, {
    id: string;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
    sortSpec?: SortSpec | null;
    processingTemplateId?: string | null;
  }>({
    mutationFn: ({ id, ...data }) =>
      apiClient.put(`/datasets/sources/${id}/processing`, data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'files'] }),
  });
}

// ── Шаблоны обработки (переиспользуемые Filter/Conversion/Sort) ──────────────────

export function useListProcessingTemplates() {
  return useQuery<DataSetProcessingTemplate[]>({
    queryKey: ['datasets', 'processing-templates'],
    queryFn: () => apiClient.get('/datasets/processing-templates').then(r => r.data),
  });
}

export function useCreateProcessingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetProcessingTemplate, Error, {
    name: string;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
    sortSpec?: SortSpec | null;
  }>({
    mutationFn: (data) => apiClient.post('/datasets/processing-templates', data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'processing-templates'] }),
  });
}

export function useUpdateProcessingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetProcessingTemplate, Error, {
    id: string;
    name: string;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
    sortSpec?: SortSpec | null;
  }>({
    mutationFn: ({ id, ...data }) => apiClient.put(`/datasets/processing-templates/${id}`, data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'processing-templates'] }),
  });
}

export function useDeleteProcessingTemplate() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.delete(`/datasets/processing-templates/${id}`).then(() => undefined),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['datasets', 'processing-templates'] });
      qc.invalidateQueries({ queryKey: ['datasets', 'files'] });
    },
  });
}

// ── Скачать оригинальный файл ─────────────────────────────────────────────────

export async function downloadDataSetFile(id: string, name: string) {
  const response = await apiClient.get(`/datasets/files/${id}/download`, { responseType: 'blob' });
  const contentDisposition = response.headers['content-disposition'] as string | undefined;
  let filename = name;
  if (contentDisposition) {
    const match = /filename\*?=['"]?(?:UTF-\d['"]*)?([^;\r\n"']*)['"]?/.exec(contentDisposition);
    if (match?.[1]) filename = decodeURIComponent(match[1].trim());
  }
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

// ── Все доступные файлы для комплекта (System+Construction+Section+Set) ───────

export function useAvailableDataSetFiles(setId: string) {
  return useQuery<DataSetFile[]>({
    queryKey: ['datasets', 'available', setId],
    queryFn: () =>
      apiClient.get('/datasets/available', { params: { setId } }).then(r => r.data),
    enabled: !!setId,
  });
}

// ── Предпросмотр ──────────────────────────────────────────────────────────────

export function usePreviewDataSetSource(sourceId: string | null, maxRows = 50) {
  return useQuery<DataSetPreview>({
    queryKey: ['datasets', 'preview', sourceId, maxRows],
    queryFn: () =>
      apiClient.get(`/datasets/sources/${sourceId}/preview`, { params: { maxRows } })
        .then(r => r.data),
    enabled: !!sourceId,
  });
}

// ── Авто-маппинг ──────────────────────────────────────────────────────────────

export function useAutoMapDataSetSource() {
  return useMutation<
    { mapping: Record<string, string> },
    Error,
    { sourceId: string; fields: { key: string; title: string }[] }
  >({
    mutationFn: ({ sourceId, fields }) =>
      apiClient.post(`/datasets/sources/${sourceId}/auto-map`, { fields }).then(r => r.data),
  });
}

// ── Привязки ──────────────────────────────────────────────────────────────────

export function useListDataSetBindings(instanceId: string) {
  return useQuery<DataSetBinding[]>({
    queryKey: ['datasets', 'bindings', instanceId],
    queryFn: () =>
      apiClient.get('/datasets/bindings', { params: { instanceId } }).then(r => r.data),
  });
}

export function useCreateDataSetBinding() {
  const qc = useQueryClient();
  return useMutation<DataSetBinding, Error, {
    instanceId: string;
    sourceId: string;
    targetFieldKey?: string | null;
    mapping?: Record<string, string>;
  }>({
    mutationFn: (data) =>
      apiClient.post('/datasets/bindings', data).then(r => r.data),
    onSuccess: (_, { instanceId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'bindings', instanceId] });
    },
  });
}

export function useUpdateDataSetBinding() {
  const qc = useQueryClient();
  return useMutation<DataSetBinding, Error, {
    id: string;
    instanceId: string;
    targetFieldKey?: string | null;
    mapping?: Record<string, string>;
  }>({
    mutationFn: ({ id, instanceId: _i, ...data }) =>
      apiClient.put(`/datasets/bindings/${id}`, data).then(r => r.data),
    onSuccess: (_, { instanceId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'bindings', instanceId] });
    },
  });
}

export function usePreviewDataSetBindings(instanceId: string) {
  return useQuery<DataSetBindingPreviewResult[]>({
    queryKey: ['datasets', 'bindings-preview', instanceId],
    queryFn: () =>
      apiClient.get('/datasets/bindings/preview', { params: { instanceId } }).then(r => r.data),
    enabled: false,
  });
}

export function useDeleteDataSetBinding() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string; instanceId: string }>({
    mutationFn: ({ id }) => apiClient.delete(`/datasets/bindings/${id}`).then(() => undefined),
    onSuccess: (_, { instanceId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'bindings', instanceId] });
    },
  });
}
