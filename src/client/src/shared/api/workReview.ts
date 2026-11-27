import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { RequiredReportField, ReviewAction, ReviewQueuePage, WorkLinePhoto, WorkMark } from './work';
import { useWorkPhotoFile } from './workPhotos';
import type { ReportInput } from './workReports';

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

export function useReviewQueue(params: ReviewQueueRequest, enabled = true) {
  return useQuery({
    queryKey: [...REVIEW_QUEUE_KEY, params],
    queryFn: ({ signal }) => readReviewQueue(params, signal),
    placeholderData: keepPreviousData,
    enabled,
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

export function correctWorkReport(report: ReviewReport, input: ReportInput, reason: string) {
  return apiClient.post<ReviewReport>(`/work/reports/${report.id}/correction`, {
    ...input, employeeId: report.employeeId, reason,
  }, { headers: { 'If-Match': report.version } }).then(response => response.data);
}

export interface MissingAcceptanceField { key: string; title: string }

export interface ReviewReportLine {
  id: string;
  workPlanItemId?: string;
  workTypeId: string;
  variantId: string | null;
  volume: number;
  unitId: string;
  sectionId: string | null;
  location: string | null;
  comment: string | null;
  isCrew: boolean;
  details: unknown;
  missingFields?: MissingAcceptanceField[];
  materials: { nomenclatureId: string; note: string | null }[];
  participants: string[];
  photos: (WorkLinePhoto & { versionUrl?: string })[];
  crewRole: string | null;
}

export interface ReviewReportVersion {
  number: number;
  reason: string;
  author: { name: string };
  at: string;
}

export interface ReviewReport {
  id: string;
  status: Exclude<ReviewDecisionResult['status'], 'draft'>;
  employeeId: string;
  constructionId: string;
  date: string;
  shift: 'day' | 'night';
  hours: number | null;
  note: string | null;
  version: string;
  lines: ReviewReportLine[];
  marks: WorkMark[];
  missingFieldsCount?: number;
  acceptWouldMark: boolean;
  currentVersion: number;
  periodClosed?: boolean;
  versions: ReviewReportVersion[];
}

interface ReviewCardHeader {
  id: string;
  employeeName: string;
  constructionName: string;
  timeZoneId: string | null;
  actions: (ReviewAction | 'correct')[];
}

export interface ReviewFullCard extends ReviewCardHeader {
  status: ReviewReport['status'];
  report: ReviewReport;
  currentRequiredMissing: RequiredReportField[];
  viewedVersion: number;
  atClosingVersion: number | null;
  workTypes: Record<string, string>;
  workVariants: Record<string, string>;
  units: Record<string, string>;
  sections: Record<string, string>;
}

export type ReviewCardData = ReviewFullCard | (ReviewCardHeader & { status: 'draft'; autoSubmitAt: string | null });
export const REVIEW_CARD_KEY = ['work', 'review-card'] as const;
export const REVIEW_JOURNAL_KEY = ['work', 'review-journal'] as const;

export function readReviewCard(id: string, number?: number, signal?: AbortSignal) {
  return apiClient.get<ReviewCardData>(`/work/review/reports/${encodeURIComponent(id)}`,
    { params: { number }, signal }).then(response => response.data);
}

export function useReviewCard(id: string, number: number | undefined) {
  return useQuery({
    queryKey: [...REVIEW_CARD_KEY, id, number],
    queryFn: ({ signal }) => readReviewCard(id, number, signal),
    retry: (count, error) => (error as { response?: { status?: number } })?.response?.status !== 404 && count < 2,
    refetchInterval: ({ state }) => {
      const status = (state.error as { response?: { status?: number } } | null)?.response?.status;
      return state.data?.status === 'draft' && status !== 403 && status !== 404 ? 15_000 : false;
    },
  });
}

export function editReviewReport(
  report: ReviewReport, changeId: string, lines: Pick<ReviewReportLine, 'id' | 'volume' | 'location' | 'details'>[],
) {
  const { id, employeeId, constructionId, date, shift, hours, note, version } = report;
  return apiClient.put<ReviewDecisionResult>(`/work/entry/reports/${id}`, {
    id, employeeId, constructionId, date, shift, hours, note, changeId,
    lines: report.lines.map((line, index) => ({
      id: line.id, workTypeId: line.workTypeId, variantId: line.variantId, unitId: line.unitId, sectionId: line.sectionId,
      comment: line.comment, isCrew: line.isCrew, details: lines[index].details, materials: line.materials,
      participants: line.participants, crewRole: line.crewRole, volume: lines[index].volume, location: lines[index].location,
    })),
  }, { headers: { 'If-Match': version } }).then(response => response.data);
}

export interface ReviewJournalEntry {
  seq: number;
  kind: string;
  author: { name: string };
  executor: { name: string };
  enteredFor: boolean;
  deviceTime: string | null;
  serverTime: string;
  comment: string | null;
}

export function readReviewJournal(id: string, signal?: AbortSignal) {
  return apiClient.get<{ items: ReviewJournalEntry[] }>(`/work/reports/${id}/journal`, { signal })
    .then(response => response.data.items);
}

export function useReviewJournal(id: string) {
  return useQuery({
    queryKey: [...REVIEW_JOURNAL_KEY, id],
    queryFn: ({ signal }) => readReviewJournal(id, signal),
  });
}

export function useReviewPhoto(photo: ReviewReportLine['photos'][number], report: string, number: number, preview: boolean) {
  const params = photo.versionUrl ? { reportId: report, number } : undefined;
  return useWorkPhotoFile(photo.id, preview, params);
}
