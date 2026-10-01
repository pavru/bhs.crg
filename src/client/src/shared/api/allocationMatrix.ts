import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import {
  INVOICES_KEY, QK, type AllocationSummaryView, type InvoiceView, type LineAllocationView,
} from './invoices';

/**
 * Быстрая разноска документа и матрица «строки × объекты» (F2, issue #1086, ТЗ COST-11, COST-12).
 *
 * ⚠️ Раскладку «поровну» и «по %» считает СЕРВЕР, форма её только рисует. Посчитай её форма —
 * «предпросмотр совпадает с применённым» сравнивал бы форму с формой и был бы зелен всегда.
 */

/**
 * Цель разноски: стройка (и, необязательно, раздел) ИЛИ статья вне строек — ровно одно (F3, issue #1087,
 * ТЗ COST-10.1). Сервер отвергает и обе цели сразу, и ни одной.
 */
export interface Place {
  construction: string | null;
  section: string | null;
  article: string | null;
}

/** Часть в наборе матрицы — ровно то, что принимает запись. */
export interface MatrixPart extends Place {
  quantity: number | null;
  amount: number | null;
}

export interface MatrixLine {
  line: string;
  parts: MatrixPart[];
}

/** Состояние матрицы целиком — тело записи. */
export interface MatrixState {
  lines: MatrixLine[];
  document: MatrixPart[];
  /**
   * Отметка версии разноски, с которой матрица ОТКРЫТА (`allocation.stamp` счёта). Кладёт её форма, а не
   * предпросмотр: сосед правил разноску, пока матрица была открыта, — запись откажет, а не вернёт удалённое.
   */
  stamp?: string;
}

/** Клетка, в которую ушёл остаток округления. `line: null` — счёт целиком. */
export interface RemainderCell extends Place {
  line: string | null;
}

/**
 * Предпросмотр — посчитан сервером, НЕ записан. Числа — в том же виде, что у записанного счёта, и
 * рисует их та же функция: поэтому показанное до применения совпадает с записанным посимвольно.
 */
export interface AllocationPreview {
  /** Что записать, если человек согласен. */
  apply: MatrixState;
  lines: Record<string, LineAllocationView>;
  summary: AllocationSummaryView;
  remainders: RemainderCell[];
}

export type SplitMethod = 'equal' | 'percent' | 'document';

export interface SplitTarget extends Place {
  percent?: number | null;
}

export function usePreviewAllocation() {
  return useMutation({
    mutationFn: ({ id, method, targets }: { id: string; method: SplitMethod; targets: SplitTarget[] }) =>
      apiClient.post<AllocationPreview>(`/costs/invoices/${id}/allocation/preview`, { method, targets })
        .then(r => r.data),
  });
}

/** Записать разноску счёта целиком. Каждая строка счёта обязана быть в наборе — иначе отказ. */
export function useReplaceMatrix() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, state }: { id: string; state: MatrixState }) =>
      apiClient.put<InvoiceView>(`/costs/invoices/${id}/allocation`, state).then(r => r.data),
    onSuccess: view => {
      qc.setQueryData([QK, view.id], view);
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}
