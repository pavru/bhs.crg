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
 * Слияние пришедшего с сервера дерева с локальным ПО БАЗЕ — предыдущему снимку сервера (issue #501).
 *
 * Запрос перечитывается по фокусу окна, поэтому безусловная синхронизация уносила бы несохранённое.
 * Но и глухой заслон «пока есть несохранённое, серверные данные ждут» не годится: `dirty` считает
 * изменённым и файл, который есть на сервере и отсутствует локально, — то есть чужой новый файл сам
 * держал бы заслон закрытым навсегда. Его не видно в списке (строки строятся из локального дерева),
 * а «Сохранить всё» отправило бы дерево БЕЗ него, и сервер удалил бы его как лишний.
 *
 * База отличает «пользователь удалил файл локально» от «файл появился на сервере»: без неё это одно
 * и то же — «есть на сервере, нет локально».
 */
export function mergeFiles(
  local: UserLibFile[], base: UserLibFile[] | null, server: UserLibFile[],
): UserLibFile[] {
  if (base === null) return server;   // первая загрузка: локального состояния ещё нет

  const baseByPath = new Map(base.map(f => [f.path, f.content]));
  const localByPath = new Map(local.map(f => [f.path, f]));
  const merged: UserLibFile[] = [];

  for (const s of server) {
    const l = localByPath.get(s.path);
    const b = baseByPath.get(s.path);
    if (l === undefined) {
      if (b === undefined) merged.push(s);   // появился на сервере — принимаем
      // иначе удалён локально: не воскрешаем, иначе удаление откатывалось бы само собой
    } else if (b === undefined || l.content !== b) {
      // Правился локально — несохранённое важнее. Отсутствие в базе тоже наш случай, а не чужой:
      // так выглядит файл, созданный локально и уже попавший на сервер, — например создали, нажали
      // Ctrl+S и продолжили печатать, пока перечитывание в пути. Взяв тут серверную копию, мы молча
      // стирали бы набранное с момента сохранения, и точки-маркера при этом не появлялось бы.
      merged.push(l);
    } else {
      merged.push(s);
    }
  }

  // Локальные, которых на сервере нет: созданные локально либо удалённые соседом, но с нашей
  // несохранённой правкой. Ни то ни другое не выбрасываем — потерять несохранённое хуже, чем
  // показать файл, которого на сервере уже нет: второе видно и исправимо, первое молчит.
  const serverPaths = new Set(server.map(f => f.path));
  for (const l of local) {
    if (serverPaths.has(l.path)) continue;
    const b = baseByPath.get(l.path);
    if (b === undefined || b !== l.content) merged.push(l);
  }

  return merged;
}

/**
 * Отпечаток сохранённого дерева (issue #502). Нужен, чтобы понимать, относится ли последняя проверка
 * собираемости к тому, что сейчас лежит на сервере: проверка — чистая функция дерева, поэтому её
 * вывод остаётся в силе ровно пока дерево то же самое.
 *
 * По ВРЕМЕНИ это не решается: сохранение само запускает перечитывание, поэтому чтение почти всегда
 * оказывается свежее проверки — и проверка «устаревала» бы через мгновение после каждого сохранения.
 * Порядок файлов не значим: сервер волен вернуть их иначе.
 */
export function treeKey(entry: string, files: UserLibFile[]): string {
  const sorted = [...files].sort((a, b) => a.path.localeCompare(b.path));
  return JSON.stringify([entry, sorted.map(f => [f.path, f.content])]);
}

/** То же для точки входа: правил локально — правка остаётся, не правил — берём серверную. */
export function mergeText(local: string, base: string | null, server: string): string {
  return base === null || local === base ? server : local;
}

/**
 * Файлы, ссылающиеся на данный относительным импортом — чтобы перед удалением или сменой пути
 * сказать поимённо, что сломается. Автоматически переписывать чужие импорты не беремся: это
 * текстовая трансформация пользовательского кода, ошибиться в ней тоньше, чем не делать.
 */
export function referencingFiles(files: UserLibFile[], target: string, entrypoint: string): string[] {
  const result: string[] = [];

  // Точку входа проверяем ПЕРВОЙ и отдельно: она чаще всех и ссылается, а адресует иначе — из корня,
  // через префикс `userlib/`, тогда как файлы дерева ссылаются друг на друга относительно себя.
  // Пока приложение само правило точку входа (#473), её отсутствие здесь было безобидным; после
  // отказа от автоматики (#492) молчание об этой ссылке означало бы, что пользователь переименует
  // файл и узнает о поломке только когда встанет генерация всех документов.
  // Путь НОРМАЛИЗУЕМ, а не сравниваем префиксом: пока строку писало приложение, она всегда была
  // канонической, но теперь импорты ведёт пользователь (#492), и «./userlib/f3.typ» — обычная
  // запись. Без нормализации ссылка не нашлась бы, и удаление файла прошло бы без предупреждения.
  const full = USERLIB_FOLDER + '/' + target;
  if (importPaths(entrypoint).some(raw => resolveRelative('', raw) === full))
    result.push(ENTRYPOINT);

  for (const file of files) {
    if (file.path === target) continue;
    const dir = file.path.includes('/') ? file.path.slice(0, file.path.lastIndexOf('/')) : '';
    for (const raw of importPaths(file.content)) {
      if (resolveRelative(dir, raw) === target) { result.push(file.path); break; }
    }
  }
  return result;
}

/**
 * Пути из `#import "…"`; координаты пакетов (`@ns/name`) — не наши файлы.
 *
 * Комментарии снимаем ДО разбора (issue #498): теперь импорты ведёт пользователь (#492), и временно
 * закомментировать строку — обычное действие. Без этого удаление файла обещало бы «на файл
 * ссылаются», а переименование предупреждало бы об импорте, которого нет.
 */
function importPaths(content: string): string[] {
  const out: string[] = [];
  const re = /#import\s+"([^"]+)"/g;
  content = stripComments(content);
  let m: RegExpExecArray | null;
  while ((m = re.exec(content)) !== null) {
    const raw = m[1].replace(/\\/g, '/');
    if (!raw.startsWith('@')) out.push(raw);
  }
  return out;
}

/** Typst-комментарии: строчные `//…` и блочные `/* … *\/`. */
function stripComments(content: string): string {
  // ОДНОЙ альтернативой, а не двумя проходами (issue #500): убрав сперва все блочные, мы позволяли
  // `/*` ВНУТРИ строчного комментария открыть мнимый блок и съесть всё до следующего `*/`. На
  // строке «// старый вариант ниже /*» это уносило импорты ниже, и удаление файла проходило без
  // предупреждения. Сервер разбирает одной альтернативой — расхождение фронта и бэка недопустимо.
  //
  // Строки в кавычках — первой альтернативой и СОХРАНЯЮТСЯ (issue #501): иначе `/*` внутри строки
  // открывал бы мнимый блок и съедал всё до следующего `*/`, а `//` в ссылке («https://…») —
  // остаток своей строки. Перенос внутри строкового литерала запрещён намеренно: на непарной
  // кавычке в разметке («Кабель "ВВГнг» и подобном) альтернатива тогда просто не совпадает, и
  // разбор идёт дальше, вместо того чтобы проглотить полфайла до следующей кавычки.
  return content.replace(/"(?:[^"\\\n]|\\.)*"|\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, m => (m[0] === '"' ? m : ''));
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
