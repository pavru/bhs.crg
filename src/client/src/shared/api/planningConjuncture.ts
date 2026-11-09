import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { HiddenPart } from './planningEstimates';
import type { EstimateImport } from './planningImports';

/**
 * Приложения к смете (change `plan-11-conjuncture`, ТЗ PLAN-9, PLAN-7, решение В-7 (а)): конъюнктурный анализ и
 * ведомость объёмов работ. Система их хранит и показывает, а не ведёт: правки нет.
 *
 * Загрузка — тем же разбором, что ЛСР: ответ — импорт в эту смету (`EstimateImport`), приложение заводит принятие
 * отчёта. Чтение — `plan.estimate.read`; цены анализа — только с `plan.estimate.cost`, без него их полей нет вовсе, а
 * корень страницы называет скрытое (`hidden`). Загрузка, принятие и удаление — `plan.estimate.edit`.
 */

/** `ka` — конъюнктурный анализ, `vor` — ведомость объёмов работ. */
export type AttachmentKind = 'ka' | 'vor';

export interface EstimateAttachment {
  id: string;
  kind: AttachmentKind;
  estimateId: string;
  fileName: string;
  number: string | null;
  name: string | null;
  programVersion: string | null;
  schemaVersion: string | null;
  creationDate: string | null;
  /** Дата текущих цен — у анализа. */
  currentPriceDate: string | null;
  /** Разобрано позиций (у ВОР — работ): приложено могло быть с согласия без не разобранных. */
  parsed: number;
  inFile: number;
  importedAt: string;
  importedBy: string | null;
  /** Версия записи: её называет удаление заголовком `If-Match`. */
  version: string;
}

export interface ConjunctureOffer {
  /** Номер по порядку у позиции, с единицы. */
  order: number;
  number: string;
  caption: string;
  units: string | null;
  /** Код группы ресурса по КСР — у позиции анализа своего кода нет. */
  resourceCode: string | null;
  supplier: string | null;
  supplierInn: string | null;
  /** Ставка НДС, % — не деньги, видна и без права. */
  vat: number | null;
  date: string | null;
  /** Это предложение дало выбранную цену позиции. */
  selected: boolean;
  /** Только с `plan.estimate.cost`. */
  cost?: { price: number | null; priceWithVat: number | null };
}

/** `min` — минимальная из предложений, `chosen` — выбрана в файле, `none` — цены нет ни у одного предложения. */
export type ConjunctureSelection = 'min' | 'chosen' | 'none';

export interface ConjunctureItem {
  id: string;
  order: number;
  chapterIndex: number;
  chapterCaption: string;
  number: string;
  caption: string;
  units: string | null;
  /** В анализе не учитывается. */
  inactive: boolean;
  selection: ConjunctureSelection;
  selectedOffer: number | null;
  offers: ConjunctureOffer[];
  /** Только с `plan.estimate.cost`. */
  cost?: { selectedPrice: number | null };
}

export interface ConjunctureItemsPage {
  total: number;
  offset: number;
  items: ConjunctureItem[];
  hidden?: HiddenPart[];
}

export interface VolumeLine {
  id: string;
  order: number;
  chapterIndex: number;
  chapterCaption: string;
  number: string;
  caption: string;
  /** Обоснование: шифр. */
  code: string | null;
  units: string | null;
  quantity: number;
  formula: string | null;
  /** Обоснование: ссылка на чертежи. */
  drawings: string | null;
  note: string | null;
  subordinate: boolean;
}

export interface VolumeLinesPage {
  total: number;
  offset: number;
  lines: VolumeLine[];
}

export interface EstimateAttached {
  attachment: EstimateAttachment;
  /** Заведено раньше тем же импортом — второго нет. */
  repeated: boolean;
}

/** Позиций на странице — предел сервера. */
export const ATTACHMENT_PAGE_LIMIT = 1000;

const KEY = ['planning', 'attachments'] as const;

const listPath = (kind: AttachmentKind, estimateId: string) =>
  `/plan/estimates/${estimateId}/${kind === 'ka' ? 'conjuncture' : 'volume-statements'}`;

const onePath = (kind: AttachmentKind, id: string) =>
  `/plan/${kind === 'ka' ? 'conjuncture' : 'volume-statements'}/${id}`;

/** Адрес файла источника — как его загрузили. */
export const attachmentFileUrl = (a: Pick<EstimateAttachment, 'kind' | 'id'>) => `/api${onePath(a.kind, a.id)}/file`;

/** Приложения сметы одного вида, новые сверху. */
export function useEstimateAttachments(kind: AttachmentKind, estimateId: string) {
  return useQuery({
    queryKey: [...KEY, kind, 'estimate', estimateId],
    queryFn: () => apiClient.get<EstimateAttachment[]>(listPath(kind, estimateId)).then(r => r.data),
  });
}

/** Позиции анализа с предложениями — одной страницей до предела сервера. */
export function useConjunctureItems(analysisId: string | null) {
  return useQuery({
    queryKey: [...KEY, 'ka', 'items', analysisId],
    queryFn: () => apiClient.get<ConjunctureItemsPage>(`${onePath('ka', analysisId!)}/items`,
      { params: { limit: ATTACHMENT_PAGE_LIMIT } }).then(r => r.data),
    enabled: analysisId !== null,
  });
}

/** Строки ведомости — одной страницей до предела сервера. */
export function useVolumeLines(statementId: string | null) {
  return useQuery({
    queryKey: [...KEY, 'vor', 'lines', statementId],
    queryFn: () => apiClient.get<VolumeLinesPage>(`${onePath('vor', statementId!)}/lines`,
      { params: { limit: ATTACHMENT_PAGE_LIMIT } }).then(r => r.data),
    enabled: statementId !== null,
  });
}

/** Загрузить файл приложения в смету: ответ — импорт в состоянии `parsing`, отчёт читается как у импорта сметы. */
export function useUploadAttachment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ estimateId, file }: { estimateId: string; file: File }) => {
      const form = new FormData();
      form.append('file', file);
      return apiClient.post<EstimateImport>(`/plan/estimates/${estimateId}/conjuncture`, form).then(r => r.data);
    },
    onSuccess: started => {
      qc.setQueryData(['planning', 'imports', 'one', started.id], started);
      void qc.invalidateQueries({ queryKey: ['planning', 'imports', 'construction', started.constructionId] });
    },
  });
}

/**
 * Приложить разобранный импорт к смете — и из окна загрузки сметы, и из загрузки приложения. `acceptPartial` —
 * согласие без не разобранных позиций; без него сервер отвечает 409 с числами. Импорт после принятия уходит.
 */
export function useAcceptAttachment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ estimateId, importId, acceptPartial }: { estimateId: string; importId: string; acceptPartial: boolean }) =>
      apiClient.post<EstimateAttached>(`/plan/estimates/${estimateId}/conjuncture/${importId}/accept`, { acceptPartial })
        .then(r => r.data),
    onSuccess: (done, { importId }) => {
      qc.removeQueries({ queryKey: ['planning', 'imports', 'one', importId] });
      void qc.invalidateQueries({ queryKey: ['planning', 'imports', 'construction'] });
      void qc.invalidateQueries({ queryKey: [...KEY, done.attachment.kind, 'estimate', done.attachment.estimateId] });
    },
  });
}

/** Удалить приложение — с версией, которую человек видел: устаревшая — 409. */
export function useDeleteAttachment() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (a: Pick<EstimateAttachment, 'kind' | 'id' | 'version' | 'estimateId'>) =>
      apiClient.delete(onePath(a.kind, a.id), { headers: { 'If-Match': a.version } }),
    onSettled: (_r, _e, a) => { void qc.invalidateQueries({ queryKey: [...KEY, a.kind, 'estimate', a.estimateId] }); },
  });
}
