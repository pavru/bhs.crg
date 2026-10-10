import { fileExtension, type FileKindsInfo } from '@/shared/api/fileKinds';

/**
 * Сохранение приложенного к счёту файла под его именем (issue #1265).
 *
 * Чем файл ПОКАЗАТЬ и какое у него расширение, решает реестр видов ядра
 * (`shared/api/fileKinds`, issue #1266): своих перечней типов здесь нет.
 */

/** Имя для сохранения. У файла без имени расширение даёт вид: без него файл не открыть щелчком. */
export function scanSaveName(
  fileName: string | null, mimeType: string | null | undefined, kinds: FileKindsInfo | undefined,
): string {
  return fileName || `Файл счёта${fileExtension(kinds, mimeType)}`;
}

/** Сохранить файл под его именем. Ссылку не отзывает: она принадлежит тому, кто её получил. */
export function saveScan(
  url: string, fileName: string | null, mimeType: string | null | undefined, kinds: FileKindsInfo | undefined,
): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = scanSaveName(fileName, mimeType, kinds);
  document.body.appendChild(link);
  link.click();
  link.remove();
}
