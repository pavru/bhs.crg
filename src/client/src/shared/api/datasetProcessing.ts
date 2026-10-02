import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { refreshSources } from './datasets';
import type {
  ColumnExprDef, ComputedColumn, DataSetProcessingTemplate, DataSetSource, RowFilterDef, SortSpec,
} from './types';

// Обработка источника и её шаблоны. Вынесено из datasets.ts: тот стоит в храповике размера, а со
// сверкой версии (issue #1141) этой теме стало тесно в общем файле.

type QueryClient = ReturnType<typeof useQueryClient>;

// ── Обработка источника (Filter/Transformation/Sort) — лёгкая правка, файл не трогает ─────

/**
 * Правка обработки ПО ЧАСТЯМ (issue #1139). Части, которой в правке нет, сервер не трогает и не
 * проверяет; `null` её сбрасывает. Диалог шлёт только то, что правит сам: досланная «за компанию»
 * часть из копии источника на странице затёрла бы то, что тем временем сохранил другой человек.
 *
 * Объединение, а не запись с необязательными полями: правку без единой части сервер отклоняет, а
 * `undefined` в запрос не попадает вовсе — тип не должен пропускать ни то, ни другое.
 */
export type SourceProcessingPatch =
  | { rowFilter: RowFilterDef | null }
  | { computedColumns: ComputedColumn[] | null }
  | { sortSpec: SortSpec | null };

/**
 * Источник тем временем изменили (409): страница показывает прежнюю обработку. Перечитываем её
 * сразу, не дожидаясь, пока человек обновит страницу сам, — значки обработки в списке перестают
 * врать. Открытому диалогу это не помогает и помогать не должно: он собран по прежней копии и
 * называет её версию, так что повторное «Сохранить» получит тот же отказ.
 */
function refreshIfMoved(qc: QueryClient, error: unknown) {
  if ((error as { response?: { status?: number } })?.response?.status === 409) void refreshSources(qc);
}

/**
 * `ifMatch` — версия обработки (`processingVersion`) той копии источника, по которой собран диалог
 * (issue #1141). Сервер сверяет её с сохранённой и отвечает 409, если источник тем временем изменили.
 *
 * ⚠️ Версию берут из той же копии, что и содержимое диалога, — в момент ОТКРЫТИЯ, а не сохранения.
 * Страница перечитывает источники и под открытым диалогом (возврат на вкладку, чужая мутация), и
 * свежая версия уехала бы на сервер вместе с черновиком, собранным по старым данным.
 */
export function useSetDataSetSourceProcessing() {
  const qc = useQueryClient();
  return useMutation<DataSetSource, Error, { id: string; ifMatch: string } & SourceProcessingPatch>({
    mutationFn: ({ id, ...data }) =>
      apiClient.put(`/datasets/sources/${id}/processing`, data).then(r => r.data),
    // Инвалидируем и предпросмотр источника (issue #399): счётчик строк и превью считаются пост-пайплайна
    // через usePreviewDataSetSource (['datasets','preview',sourceId,...]) — без этого фильтр не виден до
    // перемонтирования. Префикс-матч покрывает maxRows=1 (счётчик) и maxRows=50 (превью).
    //
    // Обещание — перечитанного списка наборов: мутация завершается (и диалог закрывается), когда
    // страница уже держит источник с новой версией. Иначе следующий диалог, открытый сразу после
    // этого, взял бы прежнюю версию — и получил бы отказ «источник изменили» на собственную правку.
    onSuccess: (_data, { id }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'preview', id] });
      qc.invalidateQueries({ queryKey: ['datasets', 'materialize-preview', id] });
      return refreshSources(qc);
    },
    onError: e => refreshIfMoved(qc, e),
  });
}

// ── Шаблоны обработки (переиспользуемые рецепты Extraction + Filter/Transformation/Sort) ──────

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
    sheetOrPath?: string | null;
    columnExpressions?: ColumnExprDef[] | null;
    rowFilter?: RowFilterDef | null;
    computedColumns?: ComputedColumn[] | null;
    sortSpec?: SortSpec | null;
  }>({
    mutationFn: (data) => apiClient.post('/datasets/processing-templates', data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'processing-templates'] }),
  });
}

/**
 * «Сохранить как шаблон» (issue #1141): шаблон собирает СЕРВЕР из сохранённого источника. Раньше
 * извлечение и обработку слала страница из своей копии — и устаревшая копия давала шаблон с
 * обработкой, которой у источника уже нет. `ifMatch` — версия той копии, которую человек видит:
 * источник изменили — 409, а не шаблон с тем, чего он не видел.
 */
export function useSaveSourceAsTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetProcessingTemplate, Error, { sourceId: string; name: string; ifMatch: string }>({
    mutationFn: ({ sourceId, ...data }) =>
      apiClient.post(`/datasets/sources/${sourceId}/processing-template`, data).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['datasets', 'processing-templates'] }),
    onError: e => refreshIfMoved(qc, e),
  });
}

export function useUpdateProcessingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetProcessingTemplate, Error, {
    id: string;
    name: string;
    sheetOrPath?: string | null;
    columnExpressions?: ColumnExprDef[] | null;
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
      void refreshSources(qc);
    },
  });
}

/** Применить шаблон (Extraction, если задана, + Filter/Transformation/Sort) к источнику — copy-on-apply. */
export function useApplyProcessingTemplate() {
  const qc = useQueryClient();
  return useMutation<DataSetSource, Error, { sourceId: string; templateId: string }>({
    mutationFn: ({ sourceId, templateId }) =>
      apiClient.post(`/datasets/sources/${sourceId}/apply-template/${templateId}`).then(r => r.data),
    // Тот же пробел, что в useSetDataSetSourceProcessing (issue #399) — освежаем предпросмотр источника.
    // И то же обещание: шаблон меняет версию обработки, и диалог, открытый до перечитывания, получил
    // бы отказ на правку поверх только что применённого шаблона.
    onSuccess: (_data, { sourceId }) => {
      qc.invalidateQueries({ queryKey: ['datasets', 'preview', sourceId] });
      qc.invalidateQueries({ queryKey: ['datasets', 'materialize-preview', sourceId] });
      return refreshSources(qc);
    },
  });
}
