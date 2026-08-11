import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Замечания внешнего анализа (issue #440). Пишет их агент через MCP; здесь их только читают и
 * разбирают — подтверждать собственные утверждения агент не может.
 *
 * Это НЕ результат проверки системы, а утверждение, ждущее человека. Оформление обязано это
 * показывать: выдать замечание за находку системы — худшее, что можно тут сделать.
 */

export type ObservationSeverity = 'Info' | 'Warning' | 'Error';
/** `Retracted` — агент сам сказал, что утверждение больше не воспроизводится (#459). */
export type ObservationStatus = 'New' | 'Confirmed' | 'Rejected' | 'Retracted';

/** На что опирается утверждение — то, по чему человек проверит его глазами. */
export interface ObservationReferences {
  documentIds?: string[];
  sourceId?: string;
  rows?: number[];
  note?: string;
  [key: string]: unknown;
}

export interface Observation {
  id: string;
  scope: string;
  scopeId: string | null;
  /** Устойчивый ключ утверждения: повтор анализа обновляет запись, а не плодит дубли. */
  key: string;
  title: string;
  detail: string | null;
  severity: ObservationSeverity;
  status: ObservationStatus;
  references: ObservationReferences;
  reportedBy: string | null;
  reviewedBy: string | null;
  /** Причину отклонения читает агент при следующем анализе. */
  reviewNote: string | null;
  reviewedAt: string | null;
  updatedAt: string;
}

const KEY = ['observations'] as const;

/**
 * Счётчики проблем («Требует разбора», бейдж вкладки комплекта, маркеры в дереве) считает
 * `/reconciliations/related` и `/reconciliations/summary` — они кэшируются под ЧУЖИМ ключом.
 * Разбор замечания меняет эти числа, поэтому каждая мутация журнала гасит и его: иначе шапка
 * остаётся протухшей до перезагрузки. На главной странице «Сверка» это долго не проявлялось —
 * там счётчик замечаний считается по самому списку, — а на вкладке комплекта видно сразу (#731).
 */
const PROBLEM_COUNTERS_KEY = ['reconciliations'] as const;

export function useObservations(scopeId?: string | null, status?: ObservationStatus) {
  return useQuery({
    queryKey: [...KEY, scopeId ?? null, status ?? null],
    queryFn: async () => (await apiClient.get<Observation[]>('/observations', {
      params: { scopeId: scopeId ?? undefined, status },
    })).data,
  });
}

export function useReviewObservation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, status, note }: {
      id: string; status: ObservationStatus; note?: string | null;
    }) => (await apiClient.put<Observation>(`/observations/${id}/review`, { status, note })).data,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: KEY });
      void qc.invalidateQueries({ queryKey: PROBLEM_COUNTERS_KEY });
    },
  });
}

export function useDeleteObservation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => { await apiClient.delete(`/observations/${id}`); },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: KEY });
      // Удаление неразобранного замечания уменьшает «Требует разбора» ровно так же, как разбор.
      void qc.invalidateQueries({ queryKey: PROBLEM_COUNTERS_KEY });
    },
  });
}

export const SEVERITY_LABELS: Record<ObservationSeverity, string> = {
  Error: 'Существенно',
  Warning: 'Внимание',
  Info: 'К сведению',
};

export const OBSERVATION_STATUS_LABELS: Record<ObservationStatus, string> = {
  New: 'Не разобрано',
  Confirmed: 'Подтверждено',
  Rejected: 'Отклонено',
  Retracted: 'Агент отозвал',
};

/** Ждёт человека. Разобранное остаётся в журнале как память, но в работу не просится. */
export function isUnreviewed(o: Observation): boolean {
  return o.status === 'New';
}
