import type { ReviewReport } from '@/shared/api/workReview';
import { detailValues } from '../reportDetails';
import { contentsOf, reportInput, type DraftLine, type ReportContents } from '../entry/reportContents';

export type CorrectionContents = ReportContents & { note: string };
export type CorrectionChoices = Record<string, 'current' | 'draft'>;

export type CorrectionConflict = {
  key: string; label: string; before: unknown; current: unknown; draft: unknown;
};

const lineFields = {
  workTypeId: 'Вид работы', variantId: 'Вариант', volume: 'Объём', unitId: 'Единица измерения',
  sectionId: 'Раздел', location: 'Место работ', comment: 'Комментарий', isCrew: 'Бригадная работа',
  crewRole: 'Роль в бригаде', materials: 'Материалы', participants: 'Участники',
} satisfies Partial<Record<keyof DraftLine, string>>;
const lineKeys = Object.keys(lineFields) as (keyof typeof lineFields)[];

export function correctionContents(report: ReviewReport): CorrectionContents {
  return { ...contentsOf(report), note: report.note ?? '' };
}

export function correctionInput(report: ReviewReport, draft: CorrectionContents) {
  // Передаём тот же состав фото как сохранённый: reportInput опускает photos во всех строках.
  const { constructionId, date, shift } = report;
  return reportInput({ constructionId, date, shift }, draft, draft.note.trim() || null, draft);
}

export function sameCorrectionValue(left: unknown, right: unknown): boolean {
  const ordered = (_key: string, value: unknown) => value && typeof value === 'object' && !Array.isArray(value)
    ? Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b))) : value;
  return JSON.stringify(left, ordered) === JSON.stringify(right, ordered);
}

export function sameCorrection(left: DraftLine, right: DraftLine) {
  return lineKeys.every(key => sameCorrectionValue(left[key], right[key]))
    && sameCorrectionValue(left.details, right.details);
}

export function reconcileCorrection(
  base: CorrectionContents, draft: CorrectionContents, current: CorrectionContents, choices: CorrectionChoices,
) {
  const conflicts: CorrectionConflict[] = [];
  function pick<T>(key: string, label: string, before: T, mine: T, after: T, equal: (a: T, b: T) => boolean = sameCorrectionValue): T {
    if (equal(before, mine)) return after;
    if (equal(before, after) || equal(mine, after)) return mine;
    conflicts.push({ key, label, before, current: after, draft: mine });
    return choices[key] === 'draft' ? mine : after;
  }

  const previous = new Map(base.lines.map(line => [line.id, line]));
  const local = new Map(draft.lines.map(line => [line.id, line]));
  const fresh = new Map(current.lines.map(line => [line.id, line]));
  const picked = new Map<string, DraftLine>();
  for (const id of new Set([...fresh.keys(), ...local.keys()])) {
    const before = previous.get(id), mine = local.get(id), after = fresh.get(id);
    const position = (before ? base.lines : mine ? draft.lines : current.lines).findIndex(line => line.id === id) + 1;
    const label = `Строка ${position}`;
    if (!before || !mine || !after) {
      const line = pick(id, label, before, mine, after, (a, b) => a && b ? sameCorrection(a, b) : a === b);
      if (line) picked.set(id, after ? { ...line, photos: after.photos } : { ...line, photos: [] });
      continue;
    }
    const line = { ...after };
    for (const key of lineKeys)
      Object.assign(line, { [key]: pick(`${id}/${key}`, `${label} · ${lineFields[key]}`, before[key], mine[key], after[key]) });
    const [beforeDetails, myDetails, currentDetails] = [before.details, mine.details, after.details].map(detailValues);
    const fields = new Set([...Object.keys(beforeDetails), ...Object.keys(myDetails), ...Object.keys(currentDetails)]);
    line.details = Object.fromEntries([...fields].map(key => [key,
      pick(`${id}/details/${key}`, `${label} · ${key}`, beforeDetails[key], myDetails[key], currentDetails[key]),
    ]).filter(([, value]) => value !== undefined));
    picked.set(id, line);
  }

  const shared = new Set(base.lines.filter(line => local.has(line.id) && fresh.has(line.id)).map(line => line.id));
  const ids = (source: CorrectionContents) => source.lines.map(line => line.id);
  const ordered = (source: CorrectionContents) => ids(source).filter(id => shared.has(id));
  const preferred = pick('order', 'Порядок строк', ordered(base), ordered(draft), ordered(current));
  const primary = sameCorrectionValue(preferred, ordered(draft)) && !sameCorrectionValue(ordered(draft), ordered(base))
    ? draft : current;
  const order = ids(primary);
  for (const other of [ids(current), ids(draft)]) {
    for (const [index, id] of other.entries()) {
      if (order.includes(id)) continue;
      const next = other.slice(index + 1).find(item => order.includes(item));
      order.splice(next === undefined ? order.length : order.indexOf(next), 0, id);
    }
  }
  return {
    conflicts,
    contents: {
      hours: pick('hours', 'Часы за смену', base.hours, draft.hours, current.hours),
      note: pick('note', 'Примечание', base.note, draft.note, current.note),
      lines: order.flatMap(id => picked.has(id) ? [picked.get(id)!] : []),
    },
  };
}

export function describeCorrectionValue(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? 'Да' : 'Нет';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

export function correctionDifferences(base: CorrectionContents, draft: CorrectionContents, workName: (id: string) => string) {
  const result: string[] = [];
  if (base.hours !== draft.hours) result.push(`Часы · ${describeCorrectionValue(base.hours)} → ${describeCorrectionValue(draft.hours)}`);
  if (base.note !== draft.note) result.push(`Примечание · ${describeCorrectionValue(draft.note)}`);
  const before = new Map(base.lines.map(line => [line.id, line]));
  const after = new Map(draft.lines.map(line => [line.id, line]));
  for (const [index, line] of draft.lines.entries()) {
    const old = before.get(line.id);
    const label = `Строка ${index + 1} · ${workName(line.workTypeId)}`;
    if (!old) result.push(`${label} · добавлена`);
    else if (!sameCorrection(old, line)) {
      const changes = lineKeys.filter(key => !sameCorrectionValue(old[key], line[key])).map(key => lineFields[key]);
      if (!sameCorrectionValue(old.details, line.details)) changes.push('Поля заказчика');
      result.push(`${label} · ${changes.join(', ')}`);
    }
  }
  for (const [index, line] of base.lines.entries())
    if (!after.has(line.id)) result.push(`Строка ${index + 1} · ${workName(line.workTypeId)} · удалена${line.photos.length ? ', фото останутся в прежней версии' : ''}`);
  if (!sameCorrectionValue(base.lines.filter(line => after.has(line.id)).map(line => line.id),
    draft.lines.filter(line => before.has(line.id)).map(line => line.id))) result.push('Изменён порядок строк.');
  return result;
}
