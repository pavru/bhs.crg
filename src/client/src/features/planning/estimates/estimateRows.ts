import type { Estimate, EstimateRevision, EstimateRow, RevisionSection } from '@/shared/api/planningEstimates';

/**
 * Правила таблицы строк редакции (change `plan-09-estimate-ui`, макет П1): отбор чипами «что ждёт», поиск,
 * раскладка по разделам ЛСР. Без отрисовки — это то, что проверяет тест.
 *
 * Носители — с сервера (D17 зонтика): отбор идёт по кодам пометок и по материалам без номенклатуры, клиент
 * не решает сам, что строка «не сопоставлена».
 */

/** Чипы «что ждёт» над таблицей (макет П1, из варианта 3). `all` — отбора нет. */
export type RowQueue = 'all' | 'unmatched' | 'no-nomenclature' | 'unit-changed' | 'remembered';

export function inQueue(row: EstimateRow, queue: RowQueue): boolean {
  switch (queue) {
    case 'all': return true;
    case 'unmatched': return row.marks.some(m => m.code === 'unmatched');
    case 'unit-changed': return row.marks.some(m => m.code === 'unit-changed');
    case 'remembered': return row.marks.some(m => m.code === 'remembered');
    // Материалы без номенклатуры — у строк ЛЮБОГО вида: и у работы с её ресурсами, и у позиции-материала.
    case 'no-nomenclature': return row.materials.some(m => m.nomenclatureId === null);
  }
}

/** Совпадает ли строка с поиском — по шифру и наименованию, без учёта регистра. */
export function matchesSearch(row: EstimateRow, query: string): boolean {
  const q = query.trim().toLocaleLowerCase('ru');
  if (!q) return true;
  return (row.rateCode ?? '').toLocaleLowerCase('ru').includes(q) || row.name.toLocaleLowerCase('ru').includes(q);
}

/** Ключ раздела: код раздела ЛСР, строки вне разделов — свой ключ (у них кода нет). */
export const OUTSIDE = '\u0000вне разделов';
export const sectionKey = (code: string | null | undefined) => code ?? OUTSIDE;

/** Строка таблицы: заголовок раздела ЛСР либо строка сметы под ним. */
export type GridLine =
  | { kind: 'section'; key: string; section: RevisionSection; open: boolean; shown: number }
  | { kind: 'row'; key: string; row: EstimateRow };

/**
 * Раскладка таблицы: разделы в порядке файла, под раскрытым — его строки. Без отбора видны все разделы
 * (по умолчанию свёрнутые, макет П1); с отбором или поиском — только разделы, где есть подходящие строки,
 * и они раскрыты: человек выбрал «что ждёт» затем, чтобы это увидеть, а не чтобы открывать разделы по одному.
 */
export function gridLines(sections: RevisionSection[], rows: EstimateRow[], queue: RowQueue, query: string,
  opened: ReadonlySet<string>): GridLine[] {
  const filtering = queue !== 'all' || query.trim() !== '';
  const bySection = new Map<string, EstimateRow[]>();
  for (const row of rows) {
    if (!inQueue(row, queue) || !matchesSearch(row, query)) continue;
    const key = sectionKey(row.lsrSection?.code);
    const list = bySection.get(key);
    if (list) list.push(row); else bySection.set(key, [row]);
  }

  const lines: GridLine[] = [];
  for (const section of sections) {
    const key = sectionKey(section.code);
    const own = bySection.get(key) ?? [];
    if (filtering && own.length === 0) continue;
    const open = filtering || opened.has(key);
    lines.push({ kind: 'section', key: `s:${key}`, section, open, shown: own.length });
    if (open) for (const row of own) lines.push({ kind: 'row', key: row.id, row });
  }
  return lines;
}

/** Номер строки на экране — сквозной № п/п из файла (решение (15) П1); у старых редакций — номер в разделе. */
export function rowNumber(row: EstimateRow): string {
  return row.number ?? row.numberInSection ?? '';
}

/** Вид сметы словами. */
export const KIND_LABEL: Record<Estimate['kind'], string> = { main: 'основная', additional: 'дополнительная' };

/** Сколько вкладок редакций видно без «ещё N» (макет П1: у живой сметы их 5–10). */
export const VISIBLE_REVISIONS = 4;

/**
 * Какие редакции стоят вкладками: действующая, последние по номеру и открытая сейчас — даже если она из
 * «ещё N». Порядок — от новой к старой, как у шкалы времени, по которой их ищут.
 */
export function visibleRevisions(estimate: Pick<Estimate, 'revisions'>, selected: string | null): {
  tabs: EstimateRevision[]; more: EstimateRevision[];
} {
  const byNumber = [...estimate.revisions].sort((a, b) => b.number - a.number);
  const keep = new Set<string>();
  const current = byNumber.find(r => r.current);
  if (current) keep.add(current.id);
  if (selected) keep.add(selected);
  for (const r of byNumber) {
    if (keep.size >= VISIBLE_REVISIONS) break;
    keep.add(r.id);
  }
  return { tabs: byNumber.filter(r => keep.has(r.id)), more: byNumber.filter(r => !keep.has(r.id)) };
}

/** Последняя по номеру редакция — та, что открывается по умолчанию: смотрят обычно на свежую. */
export function latestRevision(estimate: Pick<Estimate, 'revisions'>): EstimateRevision | null {
  return estimate.revisions.reduce<EstimateRevision | null>((best, r) => (!best || r.number > best.number ? r : best), null);
}
