import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { WorkPhotoUpload } from './work';
import type { WorkReport } from './workReports';

export interface WorkPhotoLimits {
  count: number;
  megabytes: number;
  maxBytes: number;
  countText: string;
  sizeText: string;
}

export const workPhotoLimitsKey = ['work', 'photo-limits'] as const;

export function useWorkPhotoLimits(enabled: boolean) {
  return useQuery({
    queryKey: workPhotoLimitsKey, enabled, retry: false,
    queryFn: ({ signal }) => apiClient.get<WorkPhotoLimits>('/work/photos/limits', { signal }).then(r => r.data),
  });
}

export function useWorkPhotoFile(id: string, preview: boolean, params?: { reportId: string; number: number }) {
  return useQuery({
    queryKey: ['work', 'review-photo', id, params, preview],
    queryFn: ({ signal }) => apiClient.get<Blob>(`/work/photos/${id}${preview ? '/preview' : ''}`,
      { params, responseType: 'blob', signal }).then(response => response.data),
    staleTime: 60_000,
  });
}

const linePhoto = (reportId: string, lineId: string, photoId: string) =>
  `/work/reports/${reportId}/lines/${lineId}/photos/${photoId}`;

export function uploadWorkPhoto(reportId: string, lineId: string, photoId: string, file: File, signal: AbortSignal) {
  return apiClient.put<WorkPhotoUpload>(linePhoto(reportId, lineId, photoId), file,
    { headers: { 'Content-Type': 'application/octet-stream' }, signal }).then(r => r.data);
}

export function removeWorkPhoto(report: WorkReport, lineId: string, photoId: string, changeId: string) {
  return apiClient.delete<WorkReport>(linePhoto(report.id, lineId, photoId),
    { params: { changeId }, headers: { 'If-Match': report.version } }).then(r => r.data);
}
