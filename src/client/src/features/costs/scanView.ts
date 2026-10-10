/**
 * Чем показывать приложенный к счёту файл — по виду, с которым его отдал сервер (issue #1265).
 *
 * Перечень закрыт так же, как на сервере: PDF и растровые изображения показываются, всё остальное
 * только скачивается. «Остальное» — не обязательно мусор: офисный файл, пока у него нет читаемого
 * образа, тоже здесь.
 */
export type ScanShownAs = 'pdf' | 'image' | 'download';

const PDF = 'application/pdf';
/** Что показывается. Тот же перечень у сервера (`FileKinds.Shown`) — сверяет `ScanLimitsAgreeTests`. */
export const SHOWN = ['application/pdf', 'image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp'];
/** Что предлагает выбор файла у «Приложить скан»: то, что потом будет чем показать. */
export const SHOWN_ACCEPT = SHOWN.join(',');

/**
 * @param mimeType вид, как его вернул `loadInvoiceScan`: уже без параметров и строчными.
 *
 * Сверка точным перечнем, а не `startsWith('image/')`: `image/svg+xml` — документ со сценариями, а
 * не картинка, и открытый во весь экран он выполнился бы.
 */
export function scanShownAs(mimeType: string | null | undefined): ScanShownAs {
  if (!mimeType || !SHOWN.includes(mimeType)) return 'download';
  return mimeType === PDF ? 'pdf' : 'image';
}

/** Расширения видов, которые сервер называет, — на случай файла без имени. */
const EXTENSIONS: Record<string, string> = {
  'application/pdf': '.pdf',
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/gif': '.gif',
  'image/webp': '.webp',
  'image/bmp': '.bmp',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': '.xlsx',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': '.docx',
  'application/vnd.ms-excel': '.xls',
};

/** Имя для сохранения. У файла без имени расширение даёт вид: без него файл не открыть щелчком. */
export function scanSaveName(fileName: string | null, mimeType: string | null | undefined): string {
  return fileName || `Скан счёта${EXTENSIONS[mimeType ?? ''] ?? ''}`;
}

/** Сохранить файл под его именем. Ссылку не отзывает: она принадлежит тому, кто её получил. */
export function saveScan(url: string, fileName: string | null, mimeType: string | null | undefined): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = scanSaveName(fileName, mimeType);
  document.body.appendChild(link);
  link.click();
  link.remove();
}
