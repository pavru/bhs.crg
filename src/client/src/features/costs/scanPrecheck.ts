import { kindNamed, recognizedKinds, shownKinds, sizeLimitWords, wordsOf, type FileKind, type FileKindsInfo } from '@/shared/api/fileKinds';

/**
 * Что можно сказать о файле, не читая его (#1265, #1266), и порядок отправки — одни у пакета и у одного
 * файла (costs-invoice-screen-14, D3). Отдельным файлом: их зовут и пакет, и проверка броска до выгрузки.
 */

/**
 * Что не так с файлом ещё до отправки. Повтор тут не поможет — причина в самом файле.
 *
 * @param allowed какие виды годны: заведение счёта — распознаваемые; приложение к счёту — {@link attachKinds}.
 */
export function precheck(
  file: File, kinds: FileKindsInfo | undefined, allowed: FileKind[] = recognizedKinds(kinds),
): string | null {
  // Вид файла определяет сервер, по содержимому (issue #1265). Здесь отсекается только то, что
  // браузер сам НАЗВАЛ другим видом: гнать на сервер пятьдесят мегабайт ради известного отказа
  // незачем. Файл без названного вида (так приходит PDF без расширения) идёт на сервер — решит он.
  // Что распознаётся и какого размера, говорит реестр (issue #1266); без реестра не отсекается
  // ничего, кроме пустого файла. Название сверяется вместе с синонимами: браузер зовёт один и тот
  // же вид по-разному, а сервер всё равно смотрит на содержимое.
  const named = kinds && file.type ? kindNamed(kinds, file.type) : undefined;
  if (kinds && file.type && file.type !== kinds.unknown && !(named && allowed.includes(named)))
    return `не ${wordsOf(allowed, 'или')}`;
  if (file.size === 0) return 'файл пуст';
  if (kinds && file.size > kinds.maxBytes) return `больше ${sizeLimitWords(kinds)}`;
  return null;
}

/**
 * Что прикладывается к счёту: то, что потом будет чем показать или прочитать (ревью PR #1823 вернуло прежний
 * охват `ScanFileInput`). Картинка, которая не распознаётся (GIF, WebP), показывается; Excel и Word сами не
 * показываются, но распознаются и стоят рядом с формой видом для чтения (#1270).
 */
export const attachKinds = (kinds: FileKindsInfo | undefined): FileKind[] =>
  [...new Set([...shownKinds(kinds), ...recognizedKinds(kinds)])];

/** Порядок отправки — по имени, числа внутри имени числами: `скан 2` раньше, чем `скан 10`. */
export const inOrder = (files: File[]): File[] =>
  [...files].sort((a, b) => a.name.localeCompare(b.name, 'ru', { numeric: true }));
