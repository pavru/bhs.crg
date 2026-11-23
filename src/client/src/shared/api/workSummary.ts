import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { WorkSummaryState } from './work';
import type { ReportShift, ReportStatus } from './workReports';

type ReportState = 'report' | 'zero-hours';

export type SummaryEntry = { shift: ReportShift } & (
  | { state: ReportState; reportId: string; hours?: number; status: Exclude<ReportStatus, 'draft'> }
  | { state: 'draft'; autoSubmitAt: string }
  | { state: Exclude<WorkSummaryState, ReportState | 'draft'> }
);

export interface SummaryRow {
  employeeId: string;
  employeeName: string;
  constructionId: string;
  constructionName: string;
  timeZoneId: string;
  cells: SummaryEntry[][];
  contact: { state: 'not-tracked' };
}

export interface SummaryResponse {
  days: string[];
  rows: SummaryRow[];
  constructions: { id: string; name: string }[];
  boundary: string;
  boundaryOutcome: 'all' | 'sites' | 'own' | 'own-and-sites' | 'not-assigned';
  reason: 'not-assigned' | null;
  total: number;
  offset: number;
  limit: number;
}

export interface SummaryRequest {
  from: string;
  to: string;
  construction?: string;
  offset: number;
}

export const WORK_SUMMARY_KEY = ['work', 'summary'] as const;

export function useWorkSummary(params: SummaryRequest) {
  return useQuery({
    queryKey: [...WORK_SUMMARY_KEY, params],
    enabled: !!params.from && !!params.to,
    queryFn: ({ signal }) => apiClient.get<SummaryResponse>('/work/summary', { params, signal }).then(r => r.data),
  });
}
