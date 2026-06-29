import { apiClient } from './client';

export interface FileAttachment {
  $type: 'file';
  blobPath: string;
  fileName: string;
  mimeType: string;
  size: number;
}

export function isFileAttachment(val: unknown): val is FileAttachment {
  return (
    val != null &&
    typeof val === 'object' &&
    (val as Record<string, unknown>)['$type'] === 'file' &&
    typeof (val as FileAttachment).blobPath === 'string'
  );
}

export function getFileCategory(mimeType: string): 'pdf' | 'image' | 'office' {
  if (mimeType === 'application/pdf') return 'pdf';
  if (mimeType.startsWith('image/')) return 'image';
  return 'office';
}

export async function uploadAttachment(file: File): Promise<FileAttachment> {
  const formData = new FormData();
  formData.append('file', file);
  const { data } = await apiClient.post<Omit<FileAttachment, '$type'>>('/attachments', formData);
  return { $type: 'file', ...data };
}

export async function uploadPrintForm(
  file: File,
  setId: string,
  instanceId: string,
  fieldKey: string,
): Promise<{ updatedFields: Record<string, unknown> }> {
  const formData = new FormData();
  formData.append('file', file);
  const { data } = await apiClient.post<{ updatedFields: Record<string, unknown> }>(
    `/document-sets/${setId}/documents/${instanceId}/print-form`,
    formData,
    { params: { fieldKey } },
  );
  return data;
}

export async function loadAttachmentObjectUrl(blobPath: string): Promise<{ url: string; mimeType: string }> {
  const response = await apiClient.get('/attachments', {
    params: { path: blobPath },
    responseType: 'blob',
  });
  const mimeType = (response.headers['content-type'] as string | undefined) ?? 'application/octet-stream';
  const blob = new Blob([response.data as BlobPart], { type: mimeType });
  return { url: URL.createObjectURL(blob), mimeType };
}

/** Открывает вложение в отдельной вкладке браузера (полноразмерный просмотр PDF/изображения). */
export async function openAttachmentInNewTab(blobPath: string): Promise<void> {
  // Вкладку открываем синхронно (в обработчике клика), чтобы не блокировал поп-ап-блокировщик.
  const w = window.open('', '_blank');
  try {
    const { url } = await loadAttachmentObjectUrl(blobPath);
    if (w) w.location.href = url;
    else window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  } catch (e) {
    if (w) w.close();
    throw e;
  }
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} Б`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`;
}
