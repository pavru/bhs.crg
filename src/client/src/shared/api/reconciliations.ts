import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { filenameFromContentDisposition } from './attachments';

/**
 * Сверка на непротиворечивость (issue #414, фаза Ф1).
 *
 * Арифметику и сопоставление считает сервер — ИИ в пути сравнения нет, иначе отчёт «прыгал» бы от
 * прогона к прогону. Клиент показывает находки и принимает человеческие решения.
 */

export type ComparisonOperator = 'Equal' | 'GreaterOrEqual' | 'LessOrEqual';
export type ToleranceKind = 'Absolute' | 'Percent';
export type FindingStatus = 'Match' | 'Mismatch' | 'MissingLeft' | 'MissingRight';
export type DecisionKind = 'Accepted' | 'Suppressed';
export type RunStatus = 'Running' | 'Completed' | 'Failed';

/** Одна сторона: источник и что из него брать. Строки с одним ключом суммируются. */
export interface ReconciliationSide {
  sourceId: string;
  /** Колонки доменного ключа. Порядок значим — стороны обязаны перечислять их согласованно. */
  keyColumns: string[];
  valueColumn: string;
  labelColumn?: string | null;
}

export interface ComparisonRule {
  operator: ComparisonOperator;
  tolerance: number;
  toleranceKind: ToleranceKind;
}

export interface ReconciliationSpec {
  left: ReconciliationSide;
  right: ReconciliationSide;
  comparison: ComparisonRule;
}

export interface Reconciliation {
  id: string;
  name: string;
  scope: string;
  scopeId: string | null;
  spec: ReconciliationSpec;
  updatedAt: string;
}

export interface ReconciliationRun {
  id: string;
  definitionId: string;
  status: RunStatus;
  startedAt: string;
  finishedAt: string | null;
  /** Заполнен только у неудачного прогона. Пустой список находок без него читался бы как
   *  «расхождений нет» — самое опасное недоразумение в подсистеме. */
  error: string | null;
  matchCount: number;
  mismatchCount: number;
  missingLeftCount: number;
  missingRightCount: number;
}

export interface FindingDecision {
  id: string;
  key: string;
  kind: DecisionKind;
  note: string | null;
  decidedBy: string | null;
  updatedAt: string;
}

/** Провенанс стороны: до ячейки не дотягиваем намеренно — строки из PDF приходят от зрительной модели. */
export interface FindingSideProvenance {
  sourceId: string;
  column: string;
  /** Номера строк источника, сложившихся в эту позицию. */
  rows: number[];
}

export interface Finding {
  id: string;
  /** Доменный ключ (нормализованные марка/сечение) — им же адресуется решение. */
  key: string;
  label: string;
  leftValue: number | null;
  rightValue: number | null;
  status: FindingStatus;
  provenance: { left: FindingSideProvenance | null; right: FindingSideProvenance | null };
  /** Вычислено сервером из истории прогонов, не хранится. */
  resolved: boolean;
  decision: FindingDecision | null;
}

const KEY = ['reconciliations'] as const;

export function useReconciliations(scope?: string, scopeId?: string) {
  return useQuery({
    queryKey: [...KEY, scope ?? null, scopeId ?? null],
    queryFn: async () => (await apiClient.get<Reconciliation[]>('/reconciliations', {
      params: { scope, scopeId },
    })).data,
  });
}

export function useReconciliationRuns(definitionId: string | null) {
  return useQuery({
    queryKey: [...KEY, definitionId, 'runs'],
    enabled: !!definitionId,
    queryFn: async () => (await apiClient.get<ReconciliationRun[]>(
      `/reconciliations/${definitionId}/runs`)).data,
  });
}

export function useFindings(definitionId: string | null, runId?: string | null) {
  return useQuery({
    queryKey: [...KEY, definitionId, 'findings', runId ?? 'latest'],
    enabled: !!definitionId,
    queryFn: async () => (await apiClient.get<Finding[]>(
      `/reconciliations/${definitionId}/findings`, { params: { runId: runId ?? undefined } })).data,
  });
}

export function useCreateReconciliation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: { name: string; scope: string; scopeId?: string | null; spec: ReconciliationSpec }) =>
      (await apiClient.post<Reconciliation>('/reconciliations', body)).data,
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useUpdateReconciliation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, ...body }: { id: string; name: string; spec: ReconciliationSpec }) =>
      (await apiClient.put<Reconciliation>(`/reconciliations/${id}`, body)).data,
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useDeleteReconciliation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => { await apiClient.delete(`/reconciliations/${id}`); },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useRunReconciliation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      (await apiClient.post<ReconciliationRun>(`/reconciliations/${id}/run`)).data,
    // Инвалидируем по префиксу: прогон меняет и историю, и находки всех её срезов.
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useSetDecision() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, key, kind, note }: {
      id: string; key: string; kind: DecisionKind; note?: string | null;
    }) => (await apiClient.put<FindingDecision>(`/reconciliations/${id}/decisions`,
      { key, kind, note })).data,
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useRemoveDecision() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, key }: { id: string; key: string }) => {
      await apiClient.delete(`/reconciliations/${id}/decisions`, { params: { key } });
    },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

// ─── Алиасы позиций (#446) ────────────────────────────────────────────────────

export type AliasStatus = 'Proposed' | 'Confirmed' | 'Rejected';

/**
 * Утверждение, что два по-разному записанных наименования обозначают одно и то же.
 * В сравнении участвуют ТОЛЬКО подтверждённые: неподтверждённый алиас в пути сравнения и есть
 * модель внутри арифметики — отчёт начал бы меняться сам по себе.
 */
export interface ReconciliationAlias {
  id: string;
  aliasKey: string;
  aliasLabel: string;
  canonicalKey: string;
  canonicalLabel: string;
  status: AliasStatus;
  note: string | null;
  proposedBy: string | null;
  confirmedBy: string | null;
  updatedAt: string;
}

export const ALIAS_STATUS_LABELS: Record<AliasStatus, string> = {
  Proposed: 'Предложено',
  Confirmed: 'Применяется',
  Rejected: 'Отклонено',
};

export function useAliases(status?: AliasStatus) {
  return useQuery({
    queryKey: [...KEY, 'aliases', status ?? null],
    queryFn: async () => (await apiClient.get<ReconciliationAlias[]>('/reconciliations/aliases', {
      params: { status },
    })).data,
  });
}

export function useCreateAlias() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      aliasKey: string; aliasLabel: string; canonicalKey: string; canonicalLabel: string; note?: string | null;
    }) => (await apiClient.post<ReconciliationAlias>('/reconciliations/aliases', body)).data,
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useReviewAlias() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, status, note }: { id: string; status: AliasStatus; note?: string | null }) =>
      (await apiClient.put<ReconciliationAlias>(`/reconciliations/aliases/${id}`, { status, note })).data,
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useDeleteAlias() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => { await apiClient.delete(`/reconciliations/aliases/${id}`); },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}

/** Кандидат на связывание: позиция, не нашедшая пары на другой стороне. */
export function isUnmatched(f: Finding): boolean {
  return f.status === 'MissingLeft' || f.status === 'MissingRight';
}

// ─── Представление ────────────────────────────────────────────────────────────

export const STATUS_LABELS: Record<FindingStatus, string> = {
  Match: 'Совпадает',
  Mismatch: 'Расхождение',
  MissingLeft: 'Нет слева',
  MissingRight: 'Нет справа',
};

export const OPERATOR_LABELS: Record<ComparisonOperator, string> = {
  Equal: 'равно',
  GreaterOrEqual: 'не меньше',
  LessOrEqual: 'не больше',
};

export const DECISION_LABELS: Record<DecisionKind, string> = {
  Accepted: 'Признано нормой',
  Suppressed: 'Исключено из сверки',
};

/**
 * Требует ли находка внимания. Решение человека снимает вопрос, даже если расхождение осталось: в
 * этом и смысл персистентного решения — «давальческое оборудование» не должно всплывать каждый прогон.
 */
export function needsAttention(f: Finding): boolean {
  return f.status !== 'Match' && !f.decision;
}

/** Сводка прогона одной строкой — то, что человек хочет увидеть, не читая список. */
export function runSummary(run: ReconciliationRun): string {
  if (run.status === 'Failed') return 'Прогон не выполнен';
  if (run.status === 'Running') return 'Выполняется…';
  const problems = run.mismatchCount + run.missingLeftCount + run.missingRightCount;
  return problems === 0
    ? `Расхождений нет (позиций: ${run.matchCount})`
    : `Требует внимания: ${problems} из ${problems + run.matchCount}`;
}

/**
 * «Отчёт о расхождениях» по комплекту — тот артефакт, который сегодня ведут руками (#444).
 * Две вкладки: находки сверки и замечания внешнего анализа. CSV нет намеренно — у него нет вкладок,
 * а склеить их в один лист значило бы выдать утверждения агента за результат системы.
 */
export async function downloadDiscrepancyReport(setId: string, setName: string) {
  const response = await apiClient.get(`/reconciliations/report/${setId}`, {
    params: { format: 'xlsx' }, responseType: 'blob',
  });
  const disposition = response.headers['content-disposition'] as string | undefined;
  const filename = filenameFromContentDisposition(disposition, `Отчёт о расхождениях — ${setName}.xlsx`);
  const url = URL.createObjectURL(response.data as Blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
