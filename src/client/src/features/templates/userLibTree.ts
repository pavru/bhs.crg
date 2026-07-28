import type { UserLibFile } from '@/shared/api/typstUserLib';

/** Имя папки дерева — фиксировано; из точки входа файлы адресуются через неё. */
export const USERLIB_FOLDER = 'userlib';
/** Точка входа. Лежит в корне, а не в дереве: её импортирует шаблон дословно (#353). */
export const ENTRYPOINT = 'userlib.typ';

/**
 * Строка списка файлов (issue #473). Список ПЛОСКИЙ, с отступом по глубине, а не сворачиваемое
 * дерево: смысл разрезания одного файла на много — видеть инвентарь целиком, а свёрнутые папки
 * воссоздали бы ровно ту проблему, которую мы решаем, этажом выше.
 */
export interface TreeRow {
  kind: 'folder' | 'file';
  /** Для файла — полный путь («gost/forms/f3.typ»), для папки — путь папки («gost/forms»). */
  path: string;
  /** Что показать: имя файла или последний сегмент папки. */
  label: string;
  depth: number;
}

/**
 * Плоский список строк для отрисовки: папки-заголовки в позиции, файлы с отступом.
 * Порядок устойчивый (по пути), иначе строки прыгали бы между сохранениями.
 */
export function buildRows(files: UserLibFile[]): TreeRow[] {
  // Файлы корня — первыми, папки следом: сортировка по одному лишь пути ставила бы «root.typ»
  // между содержимым «gost/» и «util/», и корневой файл терялся бы среди чужих отступов.
  const depthOf = (p: string) => (p.includes('/') ? 1 : 0);
  const sorted = [...files].sort((a, b) =>
    depthOf(a.path) - depthOf(b.path) || a.path.localeCompare(b.path, 'ru'));
  const rows: TreeRow[] = [];
  let lastFolder = '';

  for (const file of sorted) {
    const cut = file.path.lastIndexOf('/');
    const folder = cut < 0 ? '' : file.path.slice(0, cut);

    if (folder !== lastFolder) {
      // Заголовки только для НОВЫХ сегментов: переход «gost/forms» → «gost/tables» не должен
      // повторять «gost».
      const parts = folder ? folder.split('/') : [];
      const lastParts = lastFolder ? lastFolder.split('/') : [];
      let common = 0;
      while (common < parts.length && common < lastParts.length && parts[common] === lastParts[common]) common++;
      for (let i = common; i < parts.length; i++)
        rows.push({ kind: 'folder', path: parts.slice(0, i + 1).join('/'), label: parts[i], depth: i });
      lastFolder = folder;
    }

    rows.push({
      kind: 'file',
      path: file.path,
      label: file.path.slice(cut + 1),
      depth: folder ? folder.split('/').length : 0,
    });
  }

  return rows;
}

/**
 * Файлы, ссылающиеся на данный относительным импортом — чтобы перед удалением или сменой пути
 * сказать поимённо, что сломается. Автоматически переписывать чужие импорты не беремся: это
 * текстовая трансформация пользовательского кода, ошибиться в ней тоньше, чем не делать.
 */
export function referencingFiles(files: UserLibFile[], target: string): string[] {
  const result: string[] = [];
  for (const file of files) {
    if (file.path === target) continue;
    const dir = file.path.includes('/') ? file.path.slice(0, file.path.lastIndexOf('/')) : '';
    for (const raw of importPaths(file.content)) {
      if (resolveRelative(dir, raw) === target) { result.push(file.path); break; }
    }
  }
  return result;
}

/** Пути из `#import "…"`; координаты пакетов (`@ns/name`) — не наши файлы. */
function importPaths(content: string): string[] {
  const out: string[] = [];
  const re = /#import\s+"([^"]+)"/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(content)) !== null) {
    const raw = m[1].replace(/\\/g, '/');
    if (!raw.startsWith('@')) out.push(raw);
  }
  return out;
}

/** Разрешение «../../util/text.typ» от папки файла. Возвращает null, если путь уходит выше дерева. */
export function resolveRelative(baseDir: string, raw: string): string | null {
  const parts = baseDir ? baseDir.split('/') : [];
  for (const segment of raw.split('/')) {
    if (segment === '' || segment === '.') continue;
    if (segment === '..') {
      if (parts.length === 0) return null;
      parts.pop();
    } else {
      parts.push(segment);
    }
  }
  return parts.length === 0 ? null : parts.join('/');
}

/**
 * Проверка пути на стороне клиента — зеркало серверной (`UserLibPath`), чтобы отказ приходил до
 * запроса. Сервер проверяет всё равно: это подсказка, а не защита.
 */
export function validatePath(path: string, existing: string[], selfPath?: string): string | null {
  const trimmed = path.trim().replace(/\\/g, '/');
  if (!trimmed) return 'Укажите путь.';
  if (trimmed.startsWith('/')) return 'Путь должен быть относительным — без ведущего «/».';
  if (!trimmed.toLowerCase().endsWith('.typ')) return 'Файл библиотеки должен иметь расширение «.typ».';
  if (trimmed.split('/').some(s => s === '' || s === '.' || s === '..'))
    return 'Пустые сегменты и «.»/«..» в пути запрещены.';
  if (existing.some(p => p === trimmed && p !== selfPath)) return 'Такой файл уже есть.';
  // Регистр значим на Linux и не значим на Windows — расхождение вылезло бы только в продакшене.
  if (existing.some(p => p.toLowerCase() === trimmed.toLowerCase() && p !== trimmed && p !== selfPath))
    return 'Уже есть файл, отличающийся только регистром.';
  return null;
}
