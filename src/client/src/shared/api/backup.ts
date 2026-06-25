import { apiClient } from './client';
import type { RestoreReport } from './types';

export async function downloadBackup(): Promise<void> {
  const response = await apiClient.get<Blob>('/backup', { responseType: 'blob' });
  const url = URL.createObjectURL(response.data);
  const a = document.createElement('a');
  a.href = url;
  const cd = response.headers['content-disposition'] as string | undefined;
  const match = cd?.match(/filename="([^"]+)"/);
  a.download = match?.[1] ?? `crg-backup-${new Date().toISOString().slice(0, 10)}.zip`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

export async function restoreBackup(file: File): Promise<RestoreReport> {
  const formData = new FormData();
  formData.append('file', file);
  const response = await apiClient.post<RestoreReport>('/backup/restore', formData);
  return response.data;
}
