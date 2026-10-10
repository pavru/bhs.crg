/**
 * Чем показывать приложенный к счёту файл — по виду, с которым его отдал сервер (issue #1265).
 *
 * Перечень закрыт так же, как на сервере: PDF и растровые изображения показываются, всё остальное
 * только скачивается. «Остальное» — не обязательно мусор: офисный файл, пока у него нет читаемого
 * образа, тоже здесь.
 */
export type ScanShownAs = 'pdf' | 'image' | 'download';

const IMAGES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];

export function scanShownAs(mimeType: string | null | undefined): ScanShownAs {
  const kind = (mimeType ?? '').split(';')[0].trim().toLowerCase();
  if (kind === 'application/pdf') return 'pdf';
  // Точным перечнем, а не `startsWith('image/')`: `image/svg+xml` — документ со сценариями, а не
  // картинка, и открытый во весь экран он выполнился бы.
  return IMAGES.includes(kind) ? 'image' : 'download';
}

/** Сохранить файл под его именем. Ссылку не отзывает: она принадлежит тому, кто её получил. */
export function saveScan(url: string, fileName: string | null): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName ?? 'Скан счёта';
  document.body.appendChild(link);
  link.click();
  link.remove();
}
