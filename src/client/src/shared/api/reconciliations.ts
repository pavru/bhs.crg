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

/**
 * Один источник в составе стороны. Колонки у каждого СВОИ: листы шкафов называют их по-разному, и
 * требовать единообразия значило бы заставить править исходники ради сверки.
 */
export interface SideSource {
  sourceId: string;
  /** Колонки доменного ключа. Порядок значим — стороны обязаны перечислять их согласованно. */
  keyColumns: string[];
  valueColumn: string;
  labelColumn?: string | null;
}

/** Одна сторона: её источники. Строки с одним ключом суммируются по ВСЕМ источникам стороны. */
export interface ReconciliationSide extends SideSource {
  /** Свод по нескольким источникам (#450). Пусто — сторона одиночная, как в старых спеках. */
  sources?: SideSource[] | null;
}

/** Источники стороны с учётом старой формы записи: спеки в БД её ещё используют. */
export function sidePartsOf(side: ReconciliationSide): SideSource[] {
  return side.sources && side.sources.length > 0
    ? side.sources
    : [{ sourceId: side.sourceId, keyColumns: side.keyColumns, valueColumn: side.valueColumn }];
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
  /** Все источники свода (#450). Пусто — находка записана до свода, читаем поля выше. */
  parts?: { sourceId: string; column: string; rows: number[] }[] | null;
}

/**
 * Сколько строк и из скольких источников собралась сторона. Свод из четырёх листов, показанный одним
 * первым источником, был бы молча неполон.
 */
export function provenanceSummary(side: FindingSideProvenance | null): string | null {
  if (!side) return null;
  const parts = side.parts && side.parts.length > 0
    ? side.parts
    : [{ sourceId: side.sourceId, column: side.column, rows: side.rows }];
  const rows = parts.reduce((n, p) => n + (p.rows?.length ?? 0), 0);
  return parts.length > 1
    ? `${parts.length} источника, строк ${rows}`
    : `${parts[0].column}, строк ${rows}`;
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

// ─── Связанные проблемы уровня (#452) ─────────────────────────────────────────

/** Сверка, относящаяся к уровню иерархии, с числом НЕразобранных находок. */
export interface RelatedReconciliation {
  id: string;
  name: string;
  unresolvedFindings: number;
  lastRunAt: string | null;
}

export interface RelatedProblems {
  /** Что показывать в счётчике: только неразобранное — бейдж обязан обнуляться действиями человека. */
  needsAttention: number;
  unresolvedFindings: number;
  unreviewedObservations: number;
  /** Есть ли расхождение, посчитанное САМОЙ системой: красный цвет зарезервирован за арифметикой. */
  hasArithmeticProblems: boolean;
  reconciliations: RelatedReconciliation[];
}

export function useRelatedProblems(scope: 'Construction' | 'Section' | 'Set', scopeId: string | undefined) {
  return useQuery({
    queryKey: [...KEY, 'related', scope, scopeId ?? null],
    enabled: !!scopeId,
    queryFn: async () => (await apiClient.get<RelatedProblems>('/reconciliations/related', {
      params: { scope, scopeId },
    })).data,
  });
}

/** Счётчик проблем одного объекта иерархии. */
export interface ProblemCount {
  scopeId: string;
  needsAttention: number;
  hasArithmeticProblems: boolean;
}

/** Свой уровень + разбивка по детям: страница обходится ОДНИМ запросом (#454). */
export interface ProblemSummary {
  needsAttention: number;
  hasArithmeticProblems: boolean;
  children: ProblemCount[];
}

export function useProblemSummary(
  scope: 'System' | 'Construction' | 'Section' | 'Set', scopeId?: string,
) {
  return useQuery({
    queryKey: [...KEY, 'summary', scope, scopeId ?? null],
    enabled: scope === 'System' || !!scopeId,
    queryFn: async () => (await apiClient.get<ProblemSummary>('/reconciliations/summary', {
      params: { scope, scopeId },
    })).data,
  });
}

/** Счётчик ребёнка по идентификатору — маркер рисуется только когда есть что разбирать. */
export function problemOf(summary: ProblemSummary | undefined, scopeId: string): ProblemCount | undefined {
  return summary?.children.find(c => c.scopeId === scopeId);
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
