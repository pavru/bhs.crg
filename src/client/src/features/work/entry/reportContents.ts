import type { ReportHeader, ReportInput, WorkReport, WorkReportLine } from '@/shared/api/workReports';
import { formatInput } from '@/shared/format/format';
import { newUuid } from '@/shared/utils/localId';
import { parseNumber } from '@/shared/utils/parseNumber';
import { detailValues } from '../reportDetails';

export type DraftLine = Omit<WorkReportLine, 'volume' | 'missingFields'> & { volume: string };
export type ReportContents = { hours: string; lines: DraftLine[] };

// formatInput использует запятую без группировки независимо от языка показа.
const readNumber = (value: string) => parseNumber(value, 'ru-RU');

export const emptyLine = (): DraftLine => ({
  id: newUuid(), workTypeId: '', variantId: null, unitId: '', volume: '', sectionId: null, location: null,
  comment: null, details: {}, isCrew: false, crewRole: null, materials: [], participants: [], photos: [],
});

export function contentsOf(report?: Pick<WorkReport, 'hours' | 'lines'>): ReportContents {
  return {
    hours: formatInput(report?.hours),
    lines: report?.lines.map(line => ({ ...lineFields(line), volume: formatInput(line.volume), photos: line.photos })) ?? [],
  };
}

function lineFields({ id, workTypeId, variantId, unitId, sectionId, location, comment, details, isCrew, crewRole, materials, participants }:
  WorkReportLine | DraftLine) {
  return { id, workTypeId, variantId, unitId, sectionId, location, comment, details, isCrew, crewRole, materials, participants };
}

export function contentsProblem({ hours, lines }: ReportContents) {
  if (hours.trim() && readNumber(hours) === null) return 'Укажите часы отчёта числом.';
  for (const [index, line] of lines.entries()) {
    if (!line.workTypeId) return `Строка ${index + 1}: выберите вид работы.`;
    if (!line.unitId) return `Строка ${index + 1}: выберите единицу измерения.`;
    if (line.volume.trim() && readNumber(line.volume) === null) return `Строка ${index + 1}: укажите объём числом.`;
  }
}

export function reportInput(header: ReportHeader, draft: ReportContents, note: string | null, saved?: ReportContents): ReportInput {
  const previous = new Map(saved?.lines.map(line => [line.id, new Set(line.photos.map(photo => photo.id))]));
  return {
    ...header, note, hours: readNumber(draft.hours),
    lines: draft.lines.map(line => {
      const old = previous.get(line.id);
      return {
        ...lineFields(line), volume: readNumber(line.volume) ?? 0,
        location: line.location?.trim() || null, comment: line.comment?.trim() || null,
        materials: line.materials.map(material => ({ ...material, note: material.note?.trim() || null })),
        // Состав меняется при новой заявке и при снятии фото после повтора создания.
        ...((old?.size ?? 0) !== line.photos.length || line.photos.some(photo => !old?.has(photo.id))
          ? { photos: line.photos.map(({ id, originalName }) => originalName ? { id, originalName } : { id }) } : {}),
      };
    }),
  };
}

export const sameContent = (first: unknown, second: unknown) => JSON.stringify(first) === JSON.stringify(second);

// При перечитывании локальные правки накладываются на свежие строки по устойчивому id.
export function rebaseContents(base: ReportContents, local: ReportContents, fresh: ReportContents, photosLocked = false) {
  const before = new Map(base.lines.map(line => [line.id, line]));
  const edits = new Map(local.lines.map(line => [line.id, line]));
  const seen = new Set(fresh.lines.map(line => line.id));
  const lines: DraftLine[] = [];
  const restored: number[] = [];
  const conflicts: string[] = [];
  for (const line of fresh.lines) {
    const previous = before.get(line.id);
    const edited = edits.get(line.id);
    if (previous && !edited) {
      // Подтверждение удаления относилось к прежнему составу строки. Новые связанные записи надо назвать заново.
      if (!hasNewDependents(previous, line)) continue;
      restored.push(lines.length + 1);
    }
    if (!previous || !edited) { lines.push(edited ?? line); continue; }
    const number = lines.length + 1;
    const changed = Object.entries(edited).filter(([key, value]) => key !== 'materials' && key !== 'participants' && key !== 'photos'
      && key !== 'details'
      && !sameContent(previous[key as keyof DraftLine], value));
    const merged = { ...line, ...Object.fromEntries(changed) };
    if (!sameContent(previous.details, edited.details)) {
      const entries = (details: unknown) => Object.entries(detailValues(details));
      merged.details = Object.fromEntries(rebaseList(entries(previous.details), entries(edited.details), entries(line.details),
        ([key]) => key, () => conflicts.push(
          `Строка ${number}: поле заказчика изменили одновременно. Сохранено состояние сервера.`)));
    }
    merged.materials = rebaseList(previous.materials, edited.materials, line.materials, item => item.nomenclatureId, item => {
      const action = item?.note?.trim() ? `Ваше примечание: «${item.note.trim()}».`
        : item ? 'Вы оставили примечание пустым.' : 'Вы убрали материал.';
      conflicts.push(`Строка ${number}: материал изменили одновременно. Сохранено состояние сервера. ${action}`);
    });
    merged.participants = rebaseList(previous.participants, edited.participants, line.participants, id => id);
    const photos = rebasePhotos(previous.photos, edited.photos, line.photos);
    if (photosLocked && !sameContent(photos, line.photos)) {
      conflicts.push(`Строка ${number}: состав фото сохранён из отчёта, поскольку отчёт закрыт для правки.`);
    } else merged.photos = photos;
    if (!merged.isCrew && merged.participants.length > 0) {
      merged.isCrew = true;
      conflicts.push(`Строка ${number}: признак бригады сохранён, поскольку в строке есть участники из другой правки.`);
    }
    if (!merged.isCrew) merged.crewRole = null;
    lines.push(merged);
  }
  for (const line of local.lines) {
    if (!seen.has(line.id) && !sameContent(before.get(line.id), line)) lines.push(line);
  }
  return { contents: { hours: base.hours === local.hours ? fresh.hours : local.hours, lines }, restored, conflicts };
}

function rebasePhotos(base: DraftLine['photos'], local: DraftLine['photos'], fresh: DraftLine['photos']) {
  const before = new Set(base.map(photo => photo.id));
  const edited = new Set(local.map(photo => photo.id));
  const current = new Set(fresh.map(photo => photo.id));
  return [
    ...fresh.filter(photo => !before.has(photo.id) || edited.has(photo.id)),
    ...local.filter(photo => !before.has(photo.id) && !current.has(photo.id)),
  ];
}

function rebaseList<T>(base: T[], local: T[], fresh: T[], key: (item: T) => string, onConflict?: (local?: T) => void) {
  const before = new Map(base.map(item => [key(item), item]));
  const edited = new Map(local.map(item => [key(item), item]));
  const current = new Map(fresh.map(item => [key(item), item]));
  for (const id of new Set([...before.keys(), ...edited.keys()])) {
    const prior = before.get(id);
    const proposed = edited.get(id);
    const server = current.get(id);
    if (sameContent(prior, proposed)) continue;
    if (!sameContent(prior, server) && !sameContent(proposed, server)) { onConflict?.(proposed); continue; }
    if (proposed === undefined) current.delete(id);
    else current.set(id, proposed);
  }
  return [...current.values()];
}

function hasNewDependents(before: DraftLine, after: DraftLine) {
  return after.photos.some(photo => !before.photos.some(old => old.id === photo.id))
    || after.materials.some(material => !before.materials.some(old => old.nomenclatureId === material.nomenclatureId))
    || after.participants.some(person => !before.participants.includes(person));
}
