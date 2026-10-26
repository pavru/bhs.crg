import { openInNewTab, shownTypeOf } from '@/shared/ui/viewer/openInNewTab';
import { withBlobErrorBody } from '@/shared/utils/apiError';
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

/**
 * Отдать браузеру файл, пришедший Blob-ом, — временной ссылкой с `download`. Один на всё приложение
 * (ревью PR #1326): прежде тот же фрагмент жил восемью копиями, и правку поведения скачивания пришлось
 * бы вносить восемь раз. Ссылка вставляется в документ: Firefox щелчок по ссылке вне документа
 * пропускает.
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
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
 * Байты вложения — файлом с именем и типом из белого списка просмотрщика (`shownTypeOf`), для того,
 * кому нужен `Blob`, а не адрес (issue #1492): `blob:`-адрес pdf.js не принимает вовсе (CSP, D8 зонтика
 * просмотрщика). Всё вне списка получает `application/octet-stream` — браузер такой файл сохраняет,
 * а не разбирает.
 *
 * Второй рубеж рядом с серверным списком приёма, и он нужен именно здесь: `blob:`-адрес наследует
 * источник страницы, которая его создала, поэтому содержимое открывается НЕ как чужой файл, а как
 * часть приложения. Ответные заголовки сервера в этом пути не участвуют вовсе — решает тип, который
 * мы сами передадим в `new File`. Список — один на клиент и растровый: расширится серверный — этот
 * останется страховкой. Имя — из `Content-Disposition`, без него — последняя часть пути: под ним
 * файл неизвестного типа скачивается (`openAttachmentInNewTab`). Текст отказа сервера разворачивается
 * из блоба (`withBlobErrorBody`) — место показывает его, а не «Request failed».
 */
export async function loadAttachmentBlob(blobPath: string): Promise<File> {
  const response = await withBlobErrorBody(apiClient.get('/attachments', {
    params: { path: blobPath },
    responseType: 'blob',
  }));
  const declared = (response.headers['content-type'] as string | undefined) ?? '';
  const mimeType = shownTypeOf(declared) ?? 'application/octet-stream';
  const name = filenameFromContentDisposition(
    response.headers['content-disposition'] as string | undefined, blobPath.split('/').pop() || 'файл');
  return new File([response.data as BlobPart], name, { type: mimeType });
}

export async function loadAttachmentObjectUrl(blobPath: string): Promise<{ url: string; mimeType: string }> {
  const blob = await loadAttachmentBlob(blobPath);
  return { url: URL.createObjectURL(blob), mimeType: blob.type };
}

/**
 * Открывает вложение в отдельной вкладке браузера (полноразмерный просмотр PDF/изображения) — общей
 * функцией просмотрщика ([UI-27]): окно открыто синхронно, в обработчике нажатия, тип — из её белого
 * списка, ссылка отзывается с отсрочкой, отказ загрузки вкладка называет словами. Тип вне списка (скан
 * TIFF) не подписывается PDF: файл скачивается под своим именем. Отказ — и загрузки, и `TabBlocked` —
 * уходит вызывающему: место показывает его через `useOpenAttachment`.
 */
export function openAttachmentInNewTab(blobPath: string): Promise<void> {
  return openInNewTab(() => loadAttachmentBlob(blobPath), { saveUnknown: true });
}

// Размер файла пишет общий форматтер (задача N2, issue #1103); отсюда он виден по-прежнему, потому
// что спрашивают его рядом с вложениями.
export { formatBytes } from '@/shared/format/format';
