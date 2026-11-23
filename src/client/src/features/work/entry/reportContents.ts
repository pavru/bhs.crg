import type { ReportHeader, ReportInput, WorkReport, WorkReportLine } from '@/shared/api/workReports';
import { formatInput } from '@/shared/format/format';
import { newUuid } from '@/shared/utils/localId';
import { parseNumber } from '@/shared/utils/parseNumber';

export type DraftLine = Omit<WorkReportLine, 'volume'> & { volume: string };
export type ReportContents = { hours: string; lines: DraftLine[] };

export const emptyLine = (): DraftLine => ({
  id: newUuid(), workTypeId: '', variantId: null, unitId: '', volume: '', sectionId: null, location: null,
  comment: null, details: {}, isCrew: false, crewRole: null, materials: [], participants: [], photos: [],
});

export function contentsOf(report?: WorkReport): ReportContents {
  return {
    hours: formatInput(report?.hours),
    lines: report?.lines.map(line => ({ ...lineFields(line), volume: formatInput(line.volume), photos: line.photos })) ?? [],
  };
}

function lineFields({ id, workTypeId, variantId, unitId, sectionId, location, comment, details, isCrew, crewRole, materials, participants }:
  WorkReportLine | DraftLine) {
  return { id, workTypeId, variantId, unitId, sectionId, location, comment, details, isCrew, crewRole, materials, participants };
}

export function contentsProblem({ hours, lines }: ReportContents, locale: string) {
  if (hours.trim() && parseNumber(hours, locale) === null) return 'Укажите часы отчёта числом.';
  for (const [index, line] of lines.entries()) {
    if (!line.workTypeId) return `Строка ${index + 1}: выберите вид работы.`;
    if (!line.unitId) return `Строка ${index + 1}: выберите единицу измерения.`;
    if (line.volume.trim() && parseNumber(line.volume, locale) === null) return `Строка ${index + 1}: укажите объём числом.`;
  }
}

export function reportInput(header: ReportHeader, draft: ReportContents, note: string | null, locale: string): ReportInput {
  return {
    ...header, note, hours: parseNumber(draft.hours, locale),
    lines: draft.lines.map(line => ({
      ...lineFields(line), volume: parseNumber(line.volume, locale) ?? 0,
      location: line.location?.trim() || null, comment: line.comment?.trim() || null,
      materials: line.materials.map(material => ({ ...material, note: material.note?.trim() || null })),
    })),
  };
}

export const sameContent = (first: unknown, second: unknown) => JSON.stringify(first) === JSON.stringify(second);

// При перечитывании локальные правки накладываются на свежие строки по устойчивому id.
export function rebaseContents(base: ReportContents, local: ReportContents, fresh: ReportContents) {
  const before = new Map(base.lines.map(line => [line.id, line]));
  const edits = new Map(local.lines.map(line => [line.id, line]));
  const seen = new Set(fresh.lines.map(line => line.id));
  const lines: DraftLine[] = [];
  const restored: number[] = [];
  for (const line of fresh.lines) {
    const previous = before.get(line.id);
    const edited = edits.get(line.id);
    if (previous && !edited) {
      // Подтверждение удаления относилось к прежнему составу строки. Новые связанные записи надо назвать заново.
      if (!hasNewDependents(previous, line)) continue;
      restored.push(lines.length + 1);
    }
    if (!previous || !edited) { lines.push(edited ?? line); continue; }
    const changed = Object.entries(edited).filter(([key, value]) => !sameContent(previous[key as keyof DraftLine], value));
    lines.push({ ...line, ...Object.fromEntries(changed) });
  }
  for (const line of local.lines) {
    if (!seen.has(line.id) && !sameContent(before.get(line.id), line)) lines.push(line);
  }
  return { contents: { hours: base.hours === local.hours ? fresh.hours : local.hours, lines }, restored };
}

function hasNewDependents(before: DraftLine, after: DraftLine) {
  return after.photos.some(photo => !before.photos.some(old => old.id === photo.id))
    || after.materials.some(material => !before.materials.some(old => old.nomenclatureId === material.nomenclatureId))
    || after.participants.some(person => !before.participants.includes(person));
}
