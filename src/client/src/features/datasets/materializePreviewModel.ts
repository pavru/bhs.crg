import { isFileAttachment } from '@/shared/api/attachments';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

// ── Модель предпросмотра: разворот вложенного в колонки-листья (issue #393) ────────────────────
export interface LeafCol { path: string[]; key: string }
export interface LeafGroup { parentKey: string; leaves: LeafCol[] }
export interface PreviewModel {
  rows: Record<string, unknown>[];
  leaves: LeafCol[];
  groups: LeafGroup[];
  /** Подпись размотанного верхнего ключа (кейс union) над таблицей, либо '' . */
  unwrappedLabel: string;
}

/** Собирает модель предпросмотра из сырых материализованных строк (чистая, тестируемая). */
export function buildPreviewModel(
  rawRows: Record<string, unknown>[],
  titleFor: (key: string) => string | undefined = () => undefined,
): PreviewModel {
  const { rows, unwrapped } = unwrapSingleChild(rawRows);
  // Порядок листьев — натуральный (DFS): у согласованных строк материализатора соседи-поля одного
  // родителя уже идут подряд и совпадают с порядком полей типа (что и ждёт пользователь).
  const leaves = collectLeaves(rows);
  const unwrappedLabel = unwrapped
    .map((seg, i) => (i === 0 ? titleFor(seg) ?? seg : seg))
    .join(' → ');
  return { rows, leaves, groups: coalesceByParent(leaves), unwrappedLabel };
}

/** Составной объект, разворачиваемый в колонки (не null, не массив, не файл-вложение). */
function isPlainObject(v: unknown): v is Record<string, unknown> {
  return v != null && typeof v === 'object' && !Array.isArray(v) && !isFileAttachment(v);
}

/** Пока ВСЕ строки лежат под одним общим верхним ключом-объектом — разматываем его (кейс union:
 * поля варианта наружу, имя ключа — в подпись над таблицей). Возвращает размотанные строки + цепочку ключей. */
function unwrapSingleChild(rows: Record<string, unknown>[]): { rows: Record<string, unknown>[]; unwrapped: string[] } {
  const unwrapped: string[] = [];
  let cur = rows;
  while (cur.length > 0) {
    const keys0 = Object.keys(cur[0] ?? {});
    if (keys0.length !== 1) break;
    const k = keys0[0];
    if (!cur.every(r => {
      const ks = Object.keys(r ?? {});
      return ks.length === 1 && ks[0] === k && isPlainObject(r[k]);
    })) break;
    unwrapped.push(k);
    cur = cur.map(r => r[k] as Record<string, unknown>);
  }
  return { rows: cur, unwrapped };
}

/** Обходит строки, собирая пути листьев (значения-НЕ-объекты) в порядке первого появления. */
function collectLeaves(rows: Record<string, unknown>[]): LeafCol[] {
  const seen = new Set<string>();
  const cols: LeafCol[] = [];
  function walk(obj: Record<string, unknown>, prefix: string[]) {
    for (const [k, v] of Object.entries(obj)) {
      const path = [...prefix, k];
      if (isPlainObject(v)) walk(v, path);
      else {
        const key = path.join('.');
        if (!seen.has(key)) { seen.add(key); cols.push({ path, key }); }
      }
    }
  }
  for (const r of rows) walk(r ?? {}, []);
  return cols;
}

/** Схлопывает подряд идущие листья с общим родителем в группы (родитель '' = лист без родителя). */
function coalesceByParent(leaves: LeafCol[]): LeafGroup[] {
  const groups: LeafGroup[] = [];
  for (const l of leaves) {
    const pk = l.path.slice(0, -1).join('.');
    const last = groups[groups.length - 1];
    if (last && last.parentKey === pk) last.leaves.push(l);
    else groups.push({ parentKey: pk, leaves: [l] });
  }
  return groups;
}

export function getPath(row: Record<string, unknown>, path: string[]): unknown {
  let cur: unknown = row;
  for (const seg of path) {
    if (!isPlainObject(cur)) return undefined;
    cur = cur[seg];
  }
  return cur;
}
