import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

export const COMPANY_CALENDAR_EDIT = 'plan.calendar.edit';
export const SCHEDULE_READ = 'plan.schedule.read';
export const SCHEDULE_EDIT = 'plan.schedule.edit';
export const CALENDAR_READ = [SCHEDULE_READ, COMPANY_CALENDAR_EDIT] as const;

export type CalendarDayKind = 'holiday' | 'working' | 'short';
export interface CalendarDay {
  date: string;
  kind: CalendarDayKind;
}

export interface CompanyCalendarInput {
  workingWeek: number;
  hoursPerShift: number;
  days: CalendarDay[];
  importFileName?: string;
}

export interface CompanyCalendar extends Omit<CompanyCalendarInput, 'importFileName'> {
  year: number;
  configured: boolean;
  version: string;
  importFileName: string | null;
  importedAt: string | null;
  gaps: { year: number; code: string; text: string }[];
}

export interface ConstructionCalendarInput {
  workingWeek: number | null;
  shifts: number;
  days: CalendarDay[];
}

export interface ConstructionCalendar extends ConstructionCalendarInput {
  constructionId: string;
  timeZoneId: string;
  version: string;
}

export interface CalendarImportChange {
  date: string;
  previousKind: CalendarDayKind | null;
  kind: CalendarDayKind | null;
  wasWorking: boolean;
  isWorking: boolean;
}

export interface CalendarImportPreview extends CompanyCalendarInput {
  year: number;
  version: string;
  importFileName: string;
  changes: CalendarImportChange[];
}

const companyPath = (year: number) => `/plan/calendars/company/${year}`;
const sitePath = (id: string) => `/plan/calendars/constructions/${id}`;
const companyKey = (year: number) => ['planning', 'calendars', 'company', year] as const;
const siteKey = (id: string) => ['planning', 'calendars', 'construction', id] as const;

export function useCompanyCalendar(year: number, enabled: boolean) {
  return useQuery({
    queryKey: companyKey(year),
    queryFn: ({ signal }) => apiClient.get<CompanyCalendar>(companyPath(year), { signal }).then(r => r.data),
    enabled, refetchOnWindowFocus: false,
  });
}

export function useConstructionCalendar(id: string, enabled: boolean) {
  return useQuery({
    queryKey: siteKey(id),
    queryFn: ({ signal }) => apiClient.get<ConstructionCalendar>(sitePath(id), { signal }).then(r => r.data),
    enabled, refetchOnWindowFocus: false,
  });
}

export function useSaveCompanyCalendar(year: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ version, input }: { version: string; input: CompanyCalendarInput }) =>
      apiClient.put<CompanyCalendar>(companyPath(year), input, { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: async calendar => {
      const key = companyKey(year);
      await queryClient.cancelQueries({ queryKey: key });
      queryClient.setQueryData(key, calendar);
    },
  });
}

export function useSaveConstructionCalendar(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ version, input }: { version: string; input: ConstructionCalendarInput }) =>
      apiClient.put<ConstructionCalendar>(sitePath(id), input, { headers: { 'If-Match': version } }).then(r => r.data),
    onSuccess: async calendar => {
      const key = siteKey(id);
      await queryClient.cancelQueries({ queryKey: key });
      queryClient.setQueryData(key, calendar);
    },
  });
}

export function usePreviewCalendarImport(year: number) {
  return useMutation({
    mutationFn: (file: File) => {
      const body = new FormData();
      body.append('file', file);
      return apiClient.post<CalendarImportPreview>(`${companyPath(year)}/import`, body).then(r => r.data);
    },
  });
}
