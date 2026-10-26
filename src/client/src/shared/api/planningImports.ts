import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { Estimate, EstimateKind, HiddenPart, PlanMark } from './planningEstimates';

/**
 * Импорт сметы из ГРАНД-Сметы (change `plan-09-estimate-ui`, PR 2; сервер — `plan-07`, ТЗ PLAN-6, PLAN-7):
 * загрузка с разбором в фоне, отчёт разбора, создание редакции решением человека, отказ.
 *
 * Все адреса — под `plan.estimate.edit`: отчёт читает тот, кто импортирует. Итоги в отчёте — только с
 * `plan.estimate.cost`, без него их нет вовсе, а корень называет скрытое (`hidden`).
 */

/** Куда ляжет редакция: в существующую смету (`estimateId`) или в новую (`newEstimate`) — ровно одно. */
export interface ImportTarget {
  estimateId: string | null;
  /** Имя сметы-цели; `null` — её удалили. */
  estimateName: string | null;
  newEstimate: { name: string; sectionId: string | null; kind: EstimateKind } | null;
}

/** `parsing` — разбирается; `parsed` — ждёт решения; `rejected` — не разобран; `committed` — редакция создана. */
export type ImportState = 'parsing' | 'parsed' | 'rejected' | 'committed';

/** Вид документа: ЛСР, ведомость объёмов работ, конъюнктурный анализ. `null` — файл не открыт или не открылся. */
export type ImportKind = 'lsr' | 'vor' | 'ka';

export interface UnparsedPosition {
  /** Где в файле: раздел и номер позиции словами разбора. */
  place: string;
  line: number;
  reason: string;
}

/** Позиция, которая строкой редакции не станет (неактивна в файле), — в «N из M» она разобранная. */
export interface SkippedPosition {
  place: string;
  reason: string;
  text: string;
}

export interface ImportChapter {
  index: number;
  caption: string;
  rows: number;
  cost?: { total: number | null };
}

/** Сводка будущей редакции. */
export interface ImportSummary {
  lsrNumber: string | null;
  lsrName: string | null;
  method: 'bim' | 'rim' | 'rm' | 'unknown';
  chapters: ImportChapter[];
  /** Строк будущей редакции — без неактивных позиций. */
  rows: number;
  inactive: number;
  cost?: { total: number | null; estimateTotal?: number | null };
}

/** Отчёт импорта (ТЗ PLAN-7). */
export interface EstimateImport {
  id: string;
  constructionId: string;
  target: ImportTarget;
  fileName: string;
  state: ImportState;
  kind: ImportKind | null;
  kindName: string | null;
  programVersion: string | null;
  schemaVersion: string | null;
  /** Ход разбора словами очереди, пока он идёт. */
  progress: string | null;
  refusal: { code: string; text: string } | null;
  parsed: number | null;
  inFile: number | null;
  unparsed: UnparsedPosition[];
  skipped: SkippedPosition[];
  summary: ImportSummary | null;
  /** `notEstimate`, `partial`, `otherCalculation`, `computedTotals`, `interrupted` — текст сервера для человека. */
  marks: PlanMark[];
  revisionId: string | null;
  createdAt: string;
  createdBy: string | null;
  /** Версия записи импорта: её называют создание редакции и отказ заголовком `If-Match`. */
  version: string;
  hidden?: HiddenPart[];
}

/** Импорт в списке импортов стройки — без отчёта. */
export interface EstimateImportItem {
  id: string;
  target: ImportTarget;
  fileName: string;
  state: ImportState;
  kind: ImportKind | null;
  refusalCode: string | null;
  /** Разбор прерван: состояние ещё `parsing`, а исхода не будет (пометка `interrupted` отчёта). Только отменить. */
  interrupted: boolean;
  revisionId: string | null;
  createdAt: string;
  createdBy: string | null;
  version: string;
}

export interface ImportCommitted {
  import: EstimateImport;
  estimate: Estimate;
  revisionId: string;
  revisionNumber: number;
  estimateCreated: boolean;
  /** Редакция создана раньше тем же импортом — ответ называет её, второй нет. */
  repeated: boolean;
}

/** Что загрузить и куда: новая редакция существующей сметы либо новая смета (решение В-12 (а)). */
export type ImportRequest =
  | { file: File; constructionId: string; estimateId: string }
  | { file: File; constructionId: string; newEstimate: { name: string; kind: EstimateKind; sectionId: string | null } };

const KEY = ['planning', 'imports'] as const;

/** Импорт ждёт решения человека: разбирается, разобран или не разобран — но редакции из него нет. */
export const waitingDecision = (i: Pick<EstimateImportItem, 'state'>) => i.state !== 'committed';

/**
 * Импорты стройки, новые сверху. Нужны ради тех, что закрыли без решения: полоса «ждёт решения» над
 * сметами открывает их окно снова. `enabled` — у человека есть `plan.estimate.edit`: без него адрес — 403.
 */
export function useConstructionImports(constructionId: string, enabled: boolean) {
  return useQuery({
    queryKey: [...KEY, 'construction', constructionId],
    queryFn: () => apiClient.get<EstimateImportItem[]>('/plan/imports', { params: { constructionId } }).then(r => r.data),
    enabled,
  });
}

/** Сколько ждать между опросами идущего разбора: файл ЛСР разбирается секунды. */
const PARSING_POLL_MS = 1000;

/**
 * Отчёт импорта. Пока идёт разбор — перечитывается сам: ход (`progress`) — слова той же задачи очереди,
 * которую показывает индикатор фоновых задач (`/api/jobs/active`), а исход разбора ложится в сам импорт.
 */
export function useEstimateImport(id: string | null) {
  return useQuery({
    queryKey: [...KEY, 'one', id],
    queryFn: () => apiClient.get<EstimateImport>(`/plan/imports/${id}`).then(r => r.data),
    enabled: id !== null,
    refetchInterval: q => (q.state.data?.state === 'parsing' && !hasMark(q.state.data, 'interrupted') ? PARSING_POLL_MS : false),
  });
}

export const hasMark = (i: Pick<EstimateImport, 'marks'>, code: string) => i.marks.some(m => m.code === code);

/** Загрузить файл: ответ — импорт в состоянии `parsing`, разбор идёт в фоне. */
export function useStartImport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (request: ImportRequest) => {
      const form = new FormData();
      form.append('file', request.file);
      form.append('constructionId', request.constructionId);
      if ('estimateId' in request) form.append('estimateId', request.estimateId);
      else {
        form.append('newEstimate.name', request.newEstimate.name);
        form.append('newEstimate.kind', request.newEstimate.kind);
        if (request.newEstimate.sectionId) form.append('newEstimate.sectionId', request.newEstimate.sectionId);
      }
      return apiClient.post<EstimateImport>('/plan/imports', form).then(r => r.data);
    },
    onSuccess: started => {
      qc.setQueryData([...KEY, 'one', started.id], started);
      void qc.invalidateQueries({ queryKey: [...KEY, 'construction', started.constructionId] });
    },
  });
}

/**
 * Создать редакцию из импорта (ТЗ PLAN-6, PLAN-7). Версия — импорта, который человек видел: вкладка, видевшая
 * его до создания редакции в другой, получит 409, а не вторую редакцию. `acceptPartial` — явное согласие на
 * редакцию без не разобранных позиций; без него сервер отвечает 409 с числами.
 */
export function useCommitImport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, version, acceptPartial }: { id: string; version: string; acceptPartial: boolean }) =>
      apiClient.post<ImportCommitted>(`/plan/imports/${id}/commit`, { acceptPartial },
        { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: done => {
      qc.setQueryData([...KEY, 'one', done.import.id], done.import);
      void qc.invalidateQueries({ queryKey: [...KEY, 'construction', done.import.constructionId] });
      void qc.invalidateQueries({ queryKey: ['planning', 'estimates'] });
    },
    // Импорт тем временем изменили или отменили — окно перечитывает отчёт, а не повторяет по прежнему.
    onError: (_e, { id }) => { void qc.invalidateQueries({ queryKey: [...KEY, 'one', id] }); },
  });
}

/** Отказаться от импорта: запись и файл уходят, редакции нет. Отменить можно и идущий разбор. */
export function useCancelImport() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version: string; constructionId: string }) =>
      apiClient.delete(`/plan/imports/${id}`, { headers: { 'If-Match': version } }).then(() => undefined),
    onSuccess: (_r, { id, constructionId }) => {
      qc.removeQueries({ queryKey: [...KEY, 'one', id] });
      void qc.invalidateQueries({ queryKey: [...KEY, 'construction', constructionId] });
    },
    onError: (_e, { id }) => { void qc.invalidateQueries({ queryKey: [...KEY, 'one', id] }); },
  });
}
