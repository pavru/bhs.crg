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

/**
 * Относится ли последняя проверка собираемости к тому, что сейчас на сервере (issue #505 — вынесено
 * из панели, чтобы это можно было проверить тестом, а не только глазами).
 *
 * Основание — отпечаток: проверка есть чистая функция дерева, поэтому её вывод в силе, пока дерево
 * то же самое. Время — только запасной случай «перечитывание после сохранения не дошло»: тогда
 * проверка описывает дерево свежее прочитанного, и верить надо ей.
 *
 * Порядок важен и держится на том, что время проверки снимается ДО запроса сохранения: сохранение
 * само запускает перечитывание и ждёт его, поэтому отметка, снятая после, всегда была бы позже
 * `dataUpdatedAt` — и временное условие оказывалось бы истинным после КАЖДОГО сохранения, подменяя
 * собой сравнение отпечатков. Тогда чужое сохранение, легшее между нашим PUT и перечитыванием,
 * выдавалось бы за проверенное нами.
 */
export function checkStillApplies(args: {
  /** Отпечаток дерева, проверенного последним; null — проверки в этом сеансе не было. */
  checkedKey: string | null;
  checkedAt: number;
  serverKey: string;
  dataUpdatedAt: number;
}): boolean {
  const { checkedKey, checkedAt, serverKey, dataUpdatedAt } = args;
  if (checkedKey === null) return false;
  return checkedKey === serverKey || checkedAt > dataUpdatedAt;
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
      if (resolveFromTreeFile(dir, raw) === target) { result.push(file.path); break; }
    }
  }
  return result;
}

/**
 * Разрешение импорта, написанного В ФАЙЛЕ ДЕРЕВА. Отдельно от `resolveRelative`, потому что путь с
 * ведущим «/» Typst считает от КОРНЯ проекта, а не от папки файла (компилятор зовётся с `--root` на
 * временную папку, где дерево лежит в подпапке `userlib/`). Проверено на Typst 0.15.1: такой импорт
 * из файла дерева компилируется.
 *
 * Без этого «/userlib/util/text.typ» разрешался бы как относительный и получал бы приставку из папки
 * файла («gost/userlib/util/text.typ»), то есть ссылка молча не находилась бы: удаление и
 * переименование проходили бы без «На файл ссылаются…», а сохранённая библиотека переставала бы
 * собираться — вместе с генерацией всех документов (issue #505). Точка входа лежит в корне, там
 * ведущий «/» и так безвреден, а вот у файлов дерева база своя.
 */
function resolveFromTreeFile(dir: string, raw: string): string | null {
  if (!raw.startsWith('/')) return resolveRelative(dir, raw);
  const fromRoot = resolveRelative('', raw);
  const prefix = USERLIB_FOLDER + '/';
  return fromRoot?.startsWith(prefix) ? fromRoot.slice(prefix.length) : null;
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
  // #include — тоже ссылка на файл (issue #506). Пока импорты вёл не пользователь, разница была
  // неважна; теперь диалог удаления — единственная защита, и на файле, на который ссылались только
  // через #include, он говорил бы «ссылок нет».
  const re = /#(?:import|include)\s+"([^"]+)"/g;
  content = stripComments(content);
  let m: RegExpExecArray | null;
  while ((m = re.exec(content)) !== null) {
    const raw = m[1].replace(/\\/g, '/');
    if (!raw.startsWith('@')) out.push(raw);
  }
  return out;
}

/**
 * Typst-комментарии: строчные `//…` и блочные `/* … *\/`. Зеркало серверного `UserLibAnalysis`:
 * расхождение фронта и бэка в понимании одного файла недопустимо — на нём диагностика об одном и
 * том же файле расходилась бы между списком и сохранением.
 *
 * Проходом, а не регулярным выражением (issue #504), потому что блочные комментарии Typst
 * ВЛОЖЕННЫЕ: нежадное `/\*[\s\S]*?\*\/` закрывает блок на первом же внутреннем `*\/`, и в
 * `/* /* *\/ #import … *\/` импорт оставался бы живым — файл числился бы подключённым, а его имена
 * попадали бы в проверку дубликатов (то самое, от чего избавлялись в #498). Закомментировать
 * область, внутри которой уже есть комментарий, — обычное действие редактора.
 *
 * Строки в кавычках СОХРАНЯЮТСЯ (issue #501): в них лежат пути импортов, а `/*` внутри строки иначе
 * открывал бы мнимый блок, `//` в ссылке («https://…») съедал бы остаток строки. Перенос внутри
 * литерала не допускается намеренно: на непарной кавычке в разметке («Кабель "ВВГнг») мы считаем её
 * обычным символом и идём дальше, вместо того чтобы проглотить полфайла до следующей кавычки.
 *
 * Переводы строк из комментариев сохраняются: `#let` считается только в начале строки.
 */
function stripComments(content: string): string {
  let out = '';
  let depth = 0;
  let i = 0;

  while (i < content.length) {
    if (depth > 0) {
      if (content.startsWith('/*', i)) { depth++; i += 2; }
      else if (content.startsWith('*/', i)) { depth--; i += 2; }
      else { if (content[i] === '\n') out += '\n'; i++; }
      continue;
    }
    if (content.startsWith('/*', i)) { depth = 1; i += 2; continue; }
    if (content.startsWith('//', i)) {
      while (i < content.length && content[i] !== '\n') i++;
      continue;
    }
    if (content[i] === '`') {
      const end = rawSpanEnd(content, i);
      if (end > i) {
        // Содержимое СНИМАЕМ, как комментарий (строки, наоборот, сохраняем — в них пути импортов).
        // Внутри сырого блока не код: «#import» там показан, а не выполнен, и считать его ссылкой
        // значило бы обещать «на файл ссылаются» там, где ссылки нет.
        for (const ch of content.slice(i, end)) if (ch === '\n') out += '\n';
        i = end;
        continue;
      }
      // Прогон без пары переносим ЦЕЛИКОМ (issue #509). Вернувшись в сканер на второй кавычке, мы
      // открыли бы мнимый одинарный блок до следующей кавычки в файле и съели бы всё между ними.
      while (content[i] === '`') { out += '`'; i++; }
      continue;
    }
    if (content[i] === '"') {
      let j = i + 1;
      while (j < content.length && content[j] !== '"' && content[j] !== '\n') {
        j += content[j] === '\\' ? 2 : 1;
      }
      if (j < content.length && content[j] === '"') { out += content.slice(i, j + 1); i = j + 1; continue; }
    }
    out += content[i];
    i++;
  }

  return out;
}

/**
 * Конец сырого блока Typst, начатого в позиции `i` (issue #506): один и тот же прогон обратных
 * кавычек открывает и закрывает его. Внутри — не код: библиотека вправе показывать в нём синтаксис
 * с непарным `/*`, и без этой ветки такой блок открывал бы мнимый комментарий и съедал остаток
 * файла — тот же класс, что `/*` в строке (#501) и вложенные комментарии (#504), этажом ниже.
 *
 * Ровно две кавычки — это ПУСТОЙ сырой литерал, законченный сам по себе (проверено на Typst 0.15.1:
 * файл с ним компилируется). Ища ему пару, мы находили её в следующем сыром блоке файла и съедали
 * всё между ними — вместе с импортами (issue #509).
 *
 * Прогон без пары возвращает `i`: вызывающий переносит его целиком как текст. Возвращать позицию
 * второй кавычки нельзя — сканер открыл бы на ней мнимый одинарный блок.
 */
function rawSpanEnd(content: string, i: number): number {
  let n = 0;
  while (content[i + n] === '`') n++;
  if (n === 2) return i + 2;
  const close = content.indexOf('`'.repeat(n), i + n);
  return close < 0 ? i : close + n;
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
