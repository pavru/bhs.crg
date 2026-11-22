import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { ReviewQueuePage, WorkMark } from './work';

export interface ReviewQueueFilters {
  constructionId?: string;
  employeeId?: string;
  from?: string;
  to?: string;
  mark?: string;
}

export interface ReviewQueueRequest extends ReviewQueueFilters {
  offset?: number;
  limit?: number;
}

const queuePath = '/work/review/queue';
export const REVIEW_QUEUE_KEY = ['work', 'review-queue'] as const;
export const REVIEW_QUEUE_LIMIT = 50;

export function readReviewQueue(params: ReviewQueueRequest, signal?: AbortSignal) {
  return apiClient.get<ReviewQueuePage>(queuePath, { params, signal }).then(response => response.data);
}

export function useReviewQueue(params: ReviewQueueRequest) {
  return useQuery({
    queryKey: [...REVIEW_QUEUE_KEY, params],
    queryFn: ({ signal }) => readReviewQueue(params, signal),
  });
}

interface ReviewSite { id: string; name: string; kinds: string[] }

/** Стройки действующих менеджерских назначений. У очереди свой ответ с границей выдачи. */
export function useReviewSites(enabled: boolean) {
  return useQuery({
    queryKey: ['work', 'review-sites'],
    queryFn: ({ signal }) => apiClient.get<{ items: ReviewSite[] }>('/work/sites/mine', { signal })
      .then(({ data }) => data.items.filter(site => site.kinds.includes('manager'))),
    enabled,
  });
}

export interface ReviewDecisionResult {
  id: string;
  status: 'draft' | 'submitted' | 'accepted' | 'returned';
  version: string;
  marks: WorkMark[];
  replayed: boolean;
}

export function acceptWorkReport(id: string, version: string, changeId: string) {
  return apiClient.post<ReviewDecisionResult>(`/work/reports/${id}/accept`, { changeId },
    { headers: { 'If-Match': version } }).then(response => response.data);
}

export function returnWorkReport(id: string, version: string, changeId: string, comment: string) {
  return apiClient.post<ReviewDecisionResult>(`/work/reports/${id}/return`, { changeId, comment },
    { headers: { 'If-Match': version } }).then(response => response.data);
}
