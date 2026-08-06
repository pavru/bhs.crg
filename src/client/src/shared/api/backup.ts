import { apiClient } from './client';
import type { BackupSizeEstimate, RestoreReport } from './types';

/**
 * Вес копии и предел восстановления. Считается на сервере по требованию (issue #711): построение
 * манифеста плюс запрос размера на каждый файл — дёшево, но не настолько, чтобы уходить при
 * каждой загрузке страницы настроек.
 */
export async function fetchBackupSize(): Promise<BackupSizeEstimate> {
  const response = await apiClient.get<BackupSizeEstimate>('/backup/size');
  return response.data;
}

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
