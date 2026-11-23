import { queryOptions, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { hasModule, hasPermission, type AccessInfo } from './access';
import type { AssignmentCandidate } from './work';
import type { ReviewReport } from './workReview';

export type ReportShift = 'day' | 'night';
export type ReportMode = 'own' | 'entry';
export type ReportStatus = 'draft' | 'returned' | 'submitted' | 'accepted';

export interface EntrySite {
  id: string;
  name: string;
  today: string;
  timeZoneId: string;
  suggested: { date: string; shift: ReportShift; label: string };
}

export interface EntrySites {
  items: EntrySite[];
  boundary: { text: string; count: number; reason: 'not-assigned' | 'not-linked' | 'directory-missing' | null };
  employeeName?: string | null;
}

export type WorkReport = Omit<ReviewReport, 'status'> & {
  status: ReportStatus;
  shift: ReportShift;
  constructionName: string | null;
  employeeName: string | null;
  timeZoneId: string | null;
  today: string | null;
  returnComment: string | null;
  periodClosed: boolean;
  note: string | null;
  autoSubmitAt: string | null;
  enteredBy: { userId: string; name: string } | null;
};

interface ReportPage {
  items: WorkReport[];
  deleted: { id: string }[];
  cursor: number;
  more: boolean;
}

export const ownReportsKey = ['work', 'reports', 'own'] as const;
export const entrySitesQuery = (mode: ReportMode, employee?: string, date?: string) => queryOptions({
  queryKey: ['work', 'entry-sites', mode, employee, date],
  enabled: mode === 'own' || !!employee,
  queryFn: ({ signal }) => apiClient.get<EntrySites>(mode === 'own' ? '/work/sites/mine'
    : `/work/entry/reports/candidates/${employee}/sites`, { params: { date }, signal }).then(r => r.data),
});

export function useWorkReportActionLabel(access: AccessInfo | undefined, active: boolean) {
  const sites = useQuery({ ...entrySitesQuery('own'),
    enabled: active && !!access && hasModule(access, 'work') && hasPermission(access, 'work.report.own') });
  return sites.data?.items[0]?.suggested.label;
}

export function useEntryCandidates(q: string) {
  return useQuery({
    queryKey: ['work', 'entry-candidates', q],
    queryFn: ({ signal }) => apiClient.get<{ items: AssignmentCandidate[]; limit: number; more: boolean }>(
      '/work/entry/reports/candidates', { params: { q }, signal }).then(r => r.data),
  });
}

export function useOwnReports(enabled: boolean) {
  return useQuery({
    queryKey: ownReportsKey, enabled,
    queryFn: async ({ signal }) => {
      const reports = new Map<string, WorkReport>();
      let since = 0;
      for (;;) {
        const { data } = await apiClient.get<ReportPage>('/work/reports', { params: { since, limit: 500 }, signal });
        for (const row of data.items) reports.set(row.id, row);
        for (const gone of data.deleted) reports.delete(gone.id);
        if (!data.more) return [...reports.values()].sort((a, b) => b.date.localeCompare(a.date) || b.shift.localeCompare(a.shift));
        since = data.cursor;
      }
    },
  });
}

const reportBase = (mode: ReportMode) => mode === 'own' ? '/work/reports' : '/work/entry/reports';
export const reportQuery = (mode: ReportMode, id: string) => queryOptions({
  queryKey: ['work', 'report', mode, id],
  queryFn: ({ signal }) => apiClient.get<WorkReport>(`${reportBase(mode)}/${id}`, { signal }).then(r => r.data),
});

export interface ReportHeader {
  constructionId: string;
  date: string;
  shift: ReportShift;
}

export function createWorkReport(mode: ReportMode, id: string, employeeId: string | null, header: ReportHeader) {
  return apiClient.post<WorkReport>(reportBase(mode), { ...header, id, employeeId, lines: [] }).then(r => r.data);
}

export function updateWorkReport(mode: ReportMode, report: WorkReport, header: ReportHeader, changeId: string) {
  return apiClient.put<WorkReport>(`${reportBase(mode)}/${report.id}`, {
    ...header, changeId, hours: report.hours, note: report.note,
    // Сохраняем существующий состав строк. Отсутствие photos сохраняет текущий состав фото на сервере.
    lines: report.lines.map(({ photos: _photos, ...line }) => line),
  }, { headers: { 'If-Match': report.version } }).then(r => r.data);
}

export function submitWorkReport(mode: ReportMode, report: WorkReport, changeId: string) {
  return apiClient.post<WorkReport>(`${reportBase(mode)}/${report.id}/submit`, { changeId },
    { headers: { 'If-Match': report.version } }).then(r => r.data);
}
