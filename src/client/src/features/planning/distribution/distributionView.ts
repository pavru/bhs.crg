import type {
  CarryOverItem, Distribution, DistributionLine, DistributionRefusal, DistributionRule, DistributionTarget, LineWrite, Quantity,
} from '@/shared/api/planningDistribution';
import { formatCount, formatQuantity, formatTime } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Правила экрана распределения (change `plan-10-distribution`, макет П2: основа — вариант 1, итог по целям — из 2 и
 * 3). Без отрисовки — это то, что проверяет тест.
 *
 * Статусы строк, счётчики и итоги — с сервера (D17 зонтика): клиент по ним отбирает и подписывает, но сам не
 * решает, распределена ли строка.
 */

/** Чипы над таблицей. `all` — отбора нет. */
export type DistQueue = 'all' | 'undistributed' | 'partial' | 'exceptions' | 'carry-over';

/** Отбор итогом по целям: щелчок по цели в строке итога. */
export type TargetFilter = { kind: 'section'; sectionId: string } | { kind: 'whole' } | { kind: 'undistributed' } | null;

export function inDistQueue(line: DistributionLine, queue: DistQueue): boolean {
  switch (queue) {
    case 'all': return true;
    case 'undistributed': return line.status === 'undistributed';
    case 'partial': return line.status === 'partial';
    case 'exceptions': return line.source === 'parts';
    // Перенос показывается не строками, а позициями отчёта (`pendingItems`): строки источника в новой редакции нет.
    case 'carry-over': return false;
  }
}

export function inTarget(line: DistributionLine, target: TargetFilter): boolean {
  if (target === null) return true;
  switch (target.kind) {
    case 'section': return line.targets.includes(target.sectionId);
    case 'whole': return line.whole;
    case 'undistributed': return line.status === 'undistributed';
  }
}

/** Число на чипе — число строк, которые он покажет; основа — счётчики сервера. */
export function queueCounts(d: Pick<Distribution, 'counts' | 'rules' | 'carryOver'>): Record<Exclude<DistQueue, 'all'>, number> {
  return {
    undistributed: d.counts.undistributed,
    partial: d.counts.partial,
    exceptions: d.rules.reduce((n, r) => n + r.exceptions, 0),
    'carry-over': d.carryOver?.pending ?? 0,
  };
}

/** Позиции отчёта переноса, ждущие решения, — их показывает чип «Перенос: проверить». */
export const pendingItems = (d: Pick<Distribution, 'carryOver'>): CarryOverItem[] =>
  d.carryOver?.items.filter(i => i.pending) ?? [];

export function matchesLine(line: DistributionLine, query: string): boolean {
  const q = query.trim().toLocaleLowerCase('ru');
  return !q || line.name.toLocaleLowerCase('ru').includes(q) || line.number.toLocaleLowerCase('ru').includes(q);
}

/** Ключ раздела ЛСР: строки вне разделов — свой ключ (у них кода нет). */
export const OUTSIDE = '\u0000вне разделов';
export const lsrKey = (code: string | null) => code ?? OUTSIDE;

export type DistGridLine =
  | { kind: 'section'; key: string; rule: DistributionRule; open: boolean; shown: number }
  | { kind: 'row'; key: string; line: DistributionLine };

/**
 * Раскладка таблицы: разделы ЛСР в порядке ответа (порядок файла), под раскрытым — его строки. С отбором — только
 * разделы с подходящими строками, раскрытые: как у сопоставления (П1).
 */
export function distGridLines(d: Pick<Distribution, 'rules' | 'lines'>, queue: DistQueue, query: string, target: TargetFilter,
  opened: ReadonlySet<string>): DistGridLine[] {
  const filtering = queue !== 'all' || query.trim() !== '' || target !== null;
  const bySection = new Map<string, DistributionLine[]>();
  for (const line of d.lines) {
    if (!inDistQueue(line, queue) || !inTarget(line, target) || !matchesLine(line, query)) continue;
    const key = lsrKey(line.lsrSectionCode);
    const list = bySection.get(key);
    if (list) list.push(line); else bySection.set(key, [line]);
  }
  const out: DistGridLine[] = [];
  for (const rule of d.rules) {
    const key = lsrKey(rule.lsrSectionCode);
    const own = bySection.get(key) ?? [];
    if (filtering && own.length === 0) continue;
    const open = filtering || opened.has(key);
    out.push({ kind: 'section', key: `s:${key}`, rule, open, shown: own.length });
    if (open) for (const line of own) out.push({ kind: 'row', key: line.rowId, line });
  }
  return out;
}

/** Имя раздела стройки по идентификатору; удалённый — словами. */
export type SectionName = (sectionId: string) => string;

export const sectionNamer = (d: Pick<Distribution, 'targets'>): SectionName => {
  const names = new Map(d.targets.sections.map(s => [s.sectionId, s.name]));
  return id => names.get(id) ?? 'удалённый раздел';
};

/** Цель правила словами: в заголовке раздела и в снэкбаре. */
export function targetText(target: DistributionTarget | null, name: SectionName): string {
  if (target === null) return 'не распределено';
  return target.kind === 'whole' ? 'стройка в целом' : name(target.sectionId);
}

const sectionsWord = (n: number) => `${formatCount(n)} ${ruPlural(n, 'раздел', 'раздела', 'разделов')}`;

/**
 * Клетка «Распределено»: куда идёт строка. Тон neutral у всего, кроме удалённой цели (§77 F.1): «не распределено»,
 * «стройка в целом», «по разделу» — состояния, а не ошибки. Две цели и больше — «2 раздела», имя не сокращается.
 */
export function lineSummary(line: DistributionLine, rule: DistributionRule | undefined, name: SectionName,
  deleted: ReadonlySet<string>): { text: string; tone: 'neutral' | 'warning'; full?: string } {
  const lost = line.targets.some(t => deleted.has(t));
  const tone = lost ? 'warning' : 'neutral';
  if (line.source === 'rule' && rule?.target) {
    return { text: `${targetText(rule.target, name)} · по разделу`, tone };
  }
  if (line.source === 'parts') {
    const rest = line.rest && line.rest.row > 0 ? ` + стройка в целом` : '';
    const full = line.parts.map(p => `${name(p.sectionId)} — ${formatQuantity(p.quantity.row)}`).join('; ')
      + (rest ? `; стройка в целом — ${formatQuantity(line.rest!.row)}` : '');
    const text = line.targets.length === 1 ? name(line.targets[0]) + rest : sectionsWord(line.targets.length) + rest;
    return { text, tone, full };
  }
  return { text: 'не распределено', tone: 'neutral' };
}

/** Коэффициент строки в единицу плана — из её объёма в обеих единицах; нет — плана у строки нет. */
export function factorOf(line: Pick<DistributionLine, 'volume'>): number | null {
  const v = line.volume;
  return v && v.plan !== null && v.row > 0 ? v.plan / v.row : null;
}

/**
 * Число из поля ввода: запятая или точка, пробелы разрядов. Пусто — `null`, не число — `NaN`: «не набрано» и
 * «набрано не число» окно различает (первое — часть не задана, второе — ошибка поля).
 */
export function quantityOf(text: string): number | null {
  // `\s` в JS — и неразрывный пробел разрядов.
  const normalized = text.replace(/\s/g, '').replace(',', '.');
  if (normalized === '') return null;
  const value = Number(normalized);
  return Number.isFinite(value) ? value : Number.NaN;
}

/** Шесть знаков — точность хранения частей; дальше шум двоичной дроби. */
const round6 = (n: number) => Math.round(n * 1e6) / 1e6;

/** Перевод между единицами строки и плана: два связанных поля окна строки. */
export const toPlanUnits = (row: number, factor: number) => round6(row * factor);
export const toRowUnits = (plan: number, factor: number) => round6(plan / factor);

/** Пара окна строки: раздел и количество в обеих единицах, как набрано. */
export interface PartDraft {
  sectionId: string;
  row: string;
  plan: string;
}

const draftText = (d: PartDraft, unit: 'row' | 'plan') => (unit === 'plan' ? d.plan : d.row);

/**
 * Пара набрана наполовину: раздел без количества или количество без раздела. Записать её нельзя — `lineWriteOf`
 * такую пару отбросил бы молча, а без единой части запись стала бы «к правилу раздела» и стёрла бы части строки.
 */
export const incompleteDraft = (d: PartDraft, unit: 'row' | 'plan') =>
  (d.sectionId === '') !== (quantityOf(draftText(d, unit)) === null);

/**
 * Остаток «стройки в целом» и превышение — по набранному, в единице, в которой набирали. Пары без раздела не
 * считаются: они не запишутся, и остаток обязан совпасть с тем, что ляжет в «стройку в целом».
 */
export function draftBalance(drafts: readonly PartDraft[], line: Pick<DistributionLine, 'volume'>, unit: 'row' | 'plan'): {
  sum: number; rest: number; over: boolean; invalid: boolean;
} {
  const volume = unit === 'plan' ? line.volume?.plan ?? 0 : line.volume?.row ?? 0;
  let sum = 0;
  let invalid = false;
  for (const d of drafts) {
    if (d.sectionId === '') continue;
    const q = quantityOf(draftText(d, unit));
    if (q === null) continue;
    if (Number.isNaN(q) || q <= 0) { invalid = true; continue; }
    sum += q;
  }
  sum = round6(sum);
  return { sum, rest: round6(volume - sum), over: sum > volume, invalid };
}

/**
 * Набор строки из пар окна: единица одна на строку — та, в которой набирали; пустые пары не пишутся. Ни одной
 * части — `null` («к правилу раздела»): пустой массив сервер не принимает.
 */
export function lineWriteOf(rowId: string, drafts: readonly PartDraft[], unit: 'row' | 'plan'): LineWrite {
  const parts = drafts
    .filter(d => d.sectionId !== '')
    .map(d => ({ sectionId: d.sectionId, quantity: quantityOf(draftText(d, unit)) }))
    .filter((p): p is { sectionId: string; quantity: number } => p.quantity !== null && !Number.isNaN(p.quantity));
  return { rowId, unit, parts: parts.length > 0 ? parts : null };
}

/** Количество в обеих единицах текстом: «12 · 100 м (1 200 м)». */
export function bothUnits(q: Quantity, units: DistributionLine['units']): string {
  const row = `${formatQuantity(q.row)}${units.row ? ` · ${units.row}` : ''}`;
  return q.plan !== null && units.plan ? `${row} (${formatQuantity(q.plan)} ${units.plan})` : row;
}

/** «На раздел…» над выбором: строки без объёма частей не принимают — их раздел правилом. */
export function wholeToSection(lines: readonly DistributionLine[], sectionId: string): { writes: LineWrite[]; skipped: number } {
  const writes: LineWrite[] = [];
  let skipped = 0;
  for (const l of lines) {
    if (!l.splittable || !l.volume) { skipped++; continue; }
    writes.push({ rowId: l.rowId, unit: 'row', parts: [{ sectionId, quantity: l.volume.row }] });
  }
  return { writes, skipped };
}

/** Позиция отчёта переноса словами: что и откуда. */
export function itemTitle(item: CarryOverItem): string {
  if (item.subject === 'rule')
    return `Правило раздела ${item.lsrSectionCode ?? '«вне разделов»'}${item.lsrSectionName ? ` «${item.lsrSectionName}»` : ''}`;
  return `${item.number ?? item.numberInSection ?? ''} ${item.rateCode ?? ''} ${item.name ?? ''}`.replace(/\s+/g, ' ').trim();
}

export const CATEGORY_TEXT: Record<CarryOverItem['category'], string> = {
  shifted: 'сдвинулось — проверьте',
  ambiguous: 'неоднозначно — выберите строку',
  overflow: 'не поместилось в новый объём',
  lost: 'не перенесено',
};

/** «Распределение тем временем изменила Петрова О. (10:42) — перечитайте»: кто и когда — из тела 409. */
export function conflictText(r: Extract<DistributionRefusal, { kind: 'conflict' }>): string {
  if (!r.changedBy) return r.text;
  const at = r.changedAt ? ` (${formatTime(r.changedAt)})` : '';
  return `Распределение тем временем изменил(а) ${r.changedBy}${at}. Перечитайте и повторите — набранное здесь останется.`;
}
