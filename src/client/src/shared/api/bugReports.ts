import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { recentApiErrors } from './apiErrorLog';

/**
 * Сообщения об ошибках из приложения (issue #834).
 *
 * Отправка доступна любому вошедшему; список, карточка и смена статуса — только администратору
 * (сервер закрывает их политикой Admin, интерфейс просто не показывает экран остальным).
 */

export type BugReportStatus = 'New' | 'Forwarded' | 'Fixed' | 'Rejected';

export interface BugReportListItem {
  id: string;
  author: string;
  status: BugReportStatus;
  /** Первая строка сообщения. */
  summary: string;
  githubIssueNumber: number | null;
  fixedInVersion: string | null;
  hasScreenshot: boolean;
  createdAt: string;
}

export interface BugReportList {
  items: BugReportListItem[];
  /** Сколько сообщений всего: больше длины списка — часть не видна, других дорог к ней нет. */
  total: number;
}

export interface BugReportDetail {
  id: string;
  author: string;
  authorEmail: string | null;
  message: string;
  /** Техблок как его собрал клиент плюс версия сервера; null — не прислали. */
  tech: BugReportTech | null;
  screenshotBlobPath: string | null;
  status: BugReportStatus;
  /** Текст будущего issue: правка администратора либо собранная сервером заготовка. */
  issueDraft: string;
  draftEdited: boolean;
  githubIssueNumber: number | null;
  githubIssueUrl: string | null;
  fixedInVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Форма техблока — та же, что собирает `collectBugReportTech`, плюс `server` от сервера. */
export interface BugReportTech {
  version?: string;
  commit?: string;
  route?: string;
  userAgent?: string;
  viewport?: string;
  stack?: string;
  /** Откуда открыли форму: рейл, экран сбоя или тост с ошибкой. */
  origin?: string;
  apiErrors?: {
    at: string; method: string; url: string; status: number; traceId?: string; count?: number;
  }[];
  server?: { version?: string; commit?: string };
  /** Техблок не сохранён из-за размера — сервер оставил отметку вместо него. */
  dropped?: string;
}

export interface SubmitBugReport {
  message: string;
  tech: BugReportTech;
  screenshotBlobPath: string | null;
}

export async function submitBugReport(body: SubmitBugReport): Promise<{ id: string }> {
  const { data } = await apiClient.post<{ id: string }>('/bug-reports', body);
  return data;
}

/**
 * Технический контекст на момент отправки. Собирается ЗДЕСЬ, а не в форме: форму открывают из трёх
 * мест, и контекст обязан получаться одинаковый — иначе сообщение с экрана сбоя молча отличалось бы
 * составом от сообщения из бокового меню.
 */
export function collectBugReportTech(extra?: { stack?: string; origin?: string }): BugReportTech {
  return {
    route: window.location.pathname + window.location.search,
    userAgent: navigator.userAgent,
    viewport: `${window.innerWidth}×${window.innerHeight}`,
    apiErrors: recentApiErrors(),
    ...extra,
  };
}

// ── Экран администратора ───────────────────────────────────────────────────

export function useBugReports(enabled = true) {
  return useQuery({
    queryKey: ['bug-reports'],
    queryFn: () => apiClient.get<BugReportList>('/bug-reports').then(r => r.data),
    enabled,
  });
}

export function useBugReport(id: string | null) {
  return useQuery({
    queryKey: ['bug-reports', id],
    queryFn: () => apiClient.get<BugReportDetail>(`/bug-reports/${id}`).then(r => r.data),
    enabled: !!id,
  });
}

/** Общий сброс: карточка и список меняются вместе (статус виден и там, и там). */
function useReportMutation<TArgs>(
  run: (args: TArgs) => Promise<BugReportDetail>,
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: (detail) => {
      qc.setQueryData(['bug-reports', detail.id], detail);
      void qc.invalidateQueries({ queryKey: ['bug-reports'] });
    },
  });
}

export function useSaveBugReportDraft() {
  return useReportMutation(({ id, text }: { id: string; text: string }) =>
    apiClient.put<BugReportDetail>(`/bug-reports/${id}/draft`, { text }).then(r => r.data));
}

export function useMarkBugReportFixed() {
  return useReportMutation(({ id, version }: { id: string; version: string }) =>
    apiClient.post<BugReportDetail>(`/bug-reports/${id}/fixed`, { version }).then(r => r.data));
}

export function useRejectBugReport() {
  return useReportMutation((id: string) =>
    apiClient.post<BugReportDetail>(`/bug-reports/${id}/rejected`).then(r => r.data));
}

export function useReopenBugReport() {
  return useReportMutation((id: string) =>
    apiClient.post<BugReportDetail>(`/bug-reports/${id}/reopen`).then(r => r.data));
}
