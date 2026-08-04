import { apiClient } from './client';

/**
 * Имя файла из заголовка Content-Disposition. ПРЕДПОЧИТАЕМ RFC 5987 `filename*=UTF-8''<pct>`
 * (корректная кириллица) — ASCII-фолбэк `filename=` заменяет не-ASCII символы подчёркиваниями,
 * поэтому его берём только если звёздочного варианта нет. Без CD — <paramref>fallback</paramref>.
 */
export function filenameFromContentDisposition(cd: string | undefined, fallback: string): string {
  if (!cd) return fallback;
  const star = /filename\*=\s*(?:UTF-8'')?([^;\r\n]+)/i.exec(cd);
  if (star?.[1]) {
    try { return decodeURIComponent(star[1].trim().replace(/^["']|["']$/g, '')); }
    catch { /* повреждённое кодирование — падаем в ASCII-фолбэк ниже */ }
  }
  const plain = /filename=\s*"?([^";\r\n]+?)"?\s*(?:;|$)/i.exec(cd);
  return plain?.[1]?.trim() || fallback;
}

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

/**
 * Загрузка картинки поля-изображения (issue #522). Эндпоинт тот же, что у вложений, а форма значения
 * другая: `$type: "image"` вместо `"file"`.
 *
 * Дискриминатор обязан отличаться: узел вложения материализуется другим путём и несёт другой контракт
 * (`fileName`/`mimeType`/`pageCount`), тогда как картинке нужны `width`/`align`/`fit`. Свести их
 * значило бы сломать хелпер `img()` во всех шаблонах.
 */
export interface UploadedImage {
  $type: 'image';
  blobPath: string;
  originalBlobPath?: string;
  fileName: string;
  mimeType: string;
  /** Сколько весил выбранный файл и сколько весит рабочая копия — для честной строки об уменьшении. */
  sourceBytes: number;
  storedBytes: number;
}

export async function uploadImage(file: File): Promise<UploadedImage> {
  const formData = new FormData();
  formData.append('file', file);
  const { data } = await apiClient.post<Omit<UploadedImage, '$type'>>('/attachments/image', formData);
  return { $type: 'image', ...data };
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

/**
 * Типы, которые можно показывать из `blob:`-адреса. Всё остальное получает
 * `application/octet-stream` — браузер такой файл сохраняет, а не разбирает.
 *
 * Второй рубеж рядом с серверным списком приёма, и он нужен именно здесь: `blob:`-адрес наследует
 * источник страницы, которая его создала, поэтому содержимое открывается НЕ как чужой файл, а как
 * часть приложения. Ответные заголовки сервера в этом пути не участвуют вовсе — решает тип, который
 * мы сами передадим в `new Blob`. Список держим узким и растровым: расширится серверный —
 * этот останется страховкой.
 */
const VIEWABLE_TYPES = new Set([
  'application/pdf', 'image/png', 'image/jpeg', 'image/gif', 'image/webp',
]);

export async function loadAttachmentObjectUrl(blobPath: string): Promise<{ url: string; mimeType: string }> {
  const response = await apiClient.get('/attachments', {
    params: { path: blobPath },
    responseType: 'blob',
  });
  const declared = (response.headers['content-type'] as string | undefined) ?? '';
  // Отсекаем параметры вида «; charset=utf-8» — сравниваем сам тип.
  const bare = declared.split(';')[0].trim().toLowerCase();
  const mimeType = VIEWABLE_TYPES.has(bare) ? bare : 'application/octet-stream';
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
