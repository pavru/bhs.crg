import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Реестр видов файлов (issue #1266): что система умеет показать и что — распознать.
 *
 * Источник один — сервер (`GET /api/files/kinds`). Своих перечней типов экран не держит: записанный
 * рядом, он расходится с серверным на первой же правке, и молча — новый вид появляется на сервере,
 * а экран по-прежнему предлагает его «только скачать» или не даёт выбрать вовсе. Так уже было:
 * выбор файла пропускал любое изображение, а распознавались три вида из них.
 */

export interface FileKind {
  /** Каким типом сервер отдаёт файл этого вида. */
  mime: string;
  /** Как вид называется человеку. */
  label: string;
  /** Расширения с точкой — для выбора файла. */
  extensions: string[];
  /** Чем файл показывается рядом с формой; `null` — показать нечем, только скачать. */
  view: 'pdf' | 'image' | null;
  /** Распознаётся ли файл этого вида. */
  recognized: boolean;
}

export interface FileKindsInfo {
  /** Так сервер называет файл, вида которого не знает; так же его называет и браузер. */
  unknown: string;
  maxBytes: number;
  kinds: FileKind[];
}

export type FileShownAs = 'pdf' | 'image' | 'download';

/** Реестр не меняется, пока работает сервер: читается один раз на вкладку. */
export function useFileKinds() {
  return useQuery({
    queryKey: ['files', 'kinds'],
    queryFn: () => apiClient.get<FileKindsInfo>('/files/kinds').then(r => r.data),
    staleTime: Infinity,
  });
}

const find = (info: FileKindsInfo | undefined, mime: string | null | undefined) =>
  mime ? info?.kinds.find(kind => kind.mime === mime) : undefined;

/**
 * Чем показать файл с таким типом. Неизвестный реестру тип — только скачать: сверка идёт точным
 * перечнем, а не по началу строки, потому что векторная картинка — документ со сценариями, и
 * открытая во весь экран она выполнилась бы.
 *
 * ⚠️ Без реестра ответ тоже «скачать» — зовите, когда реестр загружен или уже ясно, что не загрузится.
 */
export function fileShownAs(info: FileKindsInfo | undefined, mime: string | null | undefined): FileShownAs {
  return find(info, mime)?.view ?? 'download';
}

/** Первое расширение вида — на случай файла без имени; пусто, если вид неизвестен. */
export function fileExtension(info: FileKindsInfo | undefined, mime: string | null | undefined): string {
  return find(info, mime)?.extensions[0] ?? '';
}

/**
 * Значение `accept` для выбора файла. И типы, и расширения: с машины без Office браузер типа
 * офисного файла не знает и по одному типу не предложил бы его.
 */
export function acceptOf(kinds: FileKind[]): string {
  return [...kinds.map(kind => kind.mime), ...kinds.flatMap(kind => kind.extensions)].join(',');
}

/** Перечень словами: «PDF, PNG и JPEG». Одинаково названные виды называются один раз. */
export function wordsOf(kinds: FileKind[], last = 'и'): string {
  const labels = [...new Set(kinds.map(kind => kind.label))];
  if (labels.length < 2) return labels.join('');
  return `${labels.slice(0, -1).join(', ')} ${last} ${labels[labels.length - 1]}`;
}

export const shownKinds = (info: FileKindsInfo | undefined) => info?.kinds.filter(kind => kind.view !== null) ?? [];
export const recognizedKinds = (info: FileKindsInfo | undefined) => info?.kinds.filter(kind => kind.recognized) ?? [];
