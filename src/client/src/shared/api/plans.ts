import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * План по документам (issue #796).
 *
 * План задаётся ТОЛЬКО на комплекте; раздел и стройка консолидируются сервером на лету. Поэтому
 * здесь нет ни правки плана уровня, ни кэша процентов: единственный источник — ответ сервера.
 */

export type PlanScope = 'System' | 'Construction' | 'Section' | 'Set';

/** Строка плана с фактом: сколько запланировано и сколько уже выпущено. */
export interface PlanRow {
  documentTypeId: string;
  typeName: string;
  plannedCount: number;
  actualCount: number;
}

export interface PlanProgress {
  planned: number;
  ready: number;
  needsAttention: number;
  /** Комплектов под уровнем, у которых плана нет: в процент они не входят и молчать о них нельзя. */
  setsWithoutPlan: number;
  hasPlan: boolean;
  /** null — плана нет. Это НЕ «0 %»: где не планировали, там ничего и не должно. */
  percent: number | null;
}

export interface PlanProgressOf {
  id: string;
  progress: PlanProgress;
}

export interface PlanSummary {
  own: PlanProgress;
  children: PlanProgressOf[];
}

const KEY = ['plans'] as const;

export function usePlanSummary(scope: PlanScope, scopeId?: string) {
  return useQuery({
    queryKey: [...KEY, 'summary', scope, scopeId ?? null],
    enabled: scope === 'System' || !!scopeId,
    queryFn: async () => (await apiClient.get<PlanSummary>('/plans/summary', {
      params: { scope, scopeId },
    })).data,
  });
}

/** Готовность ребёнка по идентификатору — процент рисуется только там, где план есть. */
export function planOf(summary: PlanSummary | undefined, id: string): PlanProgress | undefined {
  return summary?.children.find(c => c.id === id)?.progress;
}

export function useDocumentSetPlan(setId: string | undefined) {
  return useQuery({
    queryKey: [...KEY, 'set', setId ?? null],
    enabled: !!setId,
    queryFn: async () => (await apiClient.get<PlanRow[]>(`/document-sets/${setId}/plan`)).data,
  });
}

/**
 * Замена плана ЦЕЛИКОМ: что прислали — то и осталось. Пустой список означает «плана нет».
 *
 * Сбрасываются и сводки: процент комплекта меняет проценты раздела и стройки, а они считаются
 * сервером — оставь их в кэше, и шапка уровнем выше показывала бы вчерашнюю цифру.
 */
export function useReplaceDocumentSetPlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { setId: string; rows: { documentTypeId: string; plannedCount: number }[] }) => {
      await apiClient.put(`/document-sets/${v.setId}/plan`, v.rows);
    },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: KEY }); },
  });
}
