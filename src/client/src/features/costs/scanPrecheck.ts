import { kindNamed, recognizedKinds, sizeLimitWords, wordsOf, type FileKindsInfo } from '@/shared/api/fileKinds';

/**
 * Что можно сказать о файле, не читая его (#1265, #1266), и порядок отправки — одни у пакета и у одного
 * файла (costs-invoice-screen-14, D3). Отдельным файлом: их зовут и пакет, и проверка броска до выгрузки.
 */

/** Что не так с файлом ещё до отправки. Повтор тут не поможет — причина в самом файле. */
export function precheck(file: File, kinds: FileKindsInfo | undefined): string | null {
  // Вид файла определяет сервер, по содержимому (issue #1265). Здесь отсекается только то, что
  // браузер сам НАЗВАЛ другим видом: гнать на сервер пятьдесят мегабайт ради известного отказа
  // незачем. Файл без названного вида (так приходит PDF без расширения) идёт на сервер — решит он.
  // Что распознаётся и какого размера, говорит реестр (issue #1266); без реестра не отсекается
  // ничего, кроме пустого файла. Название сверяется вместе с синонимами: браузер зовёт один и тот
  // же вид по-разному, а сервер всё равно смотрит на содержимое.
  if (kinds && file.type && file.type !== kinds.unknown && !kindNamed(kinds, file.type)?.recognized)
    return `не ${wordsOf(recognizedKinds(kinds), 'или')}`;
  if (file.size === 0) return 'файл пуст';
  if (kinds && file.size > kinds.maxBytes) return `больше ${sizeLimitWords(kinds)}`;
  return null;
}

/** Порядок отправки — по имени, числа внутри имени числами: `скан 2` раньше, чем `скан 10`. */
export const inOrder = (files: File[]): File[] =>
  [...files].sort((a, b) => a.name.localeCompare(b.name, 'ru', { numeric: true }));
