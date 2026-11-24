import type { ScheduleAssignee, ScheduleWork, ScheduleWorkInput } from '@/shared/api/planningScheduleTypes';
import { formatInput } from '@/shared/format/format';
import { parseNumber } from '@/shared/utils/parseNumber';

export interface WorkDraft {
  volume: string;
  start: string;
  end: string;
  employeeIds: string[];
  assignees: ScheduleAssignee[];
}

export const emptyDraft: WorkDraft = { volume: '', start: '', end: '', employeeIds: [], assignees: [] };

export function draftOf(work: ScheduleWork): WorkDraft {
  return {
    volume: formatInput(work.volume), start: work.start ?? '', end: work.end ?? '',
    employeeIds: work.employeeIds, assignees: work.assignees,
  };
}

const sameEmployees = (a: string[], b: string[]) => a.length === b.length && a.every(id => b.includes(id));

export function draftChanged(original: WorkDraft, draft: WorkDraft) {
  return original.volume !== draft.volume || original.start !== draft.start || original.end !== draft.end
    || !sameEmployees(original.employeeIds, draft.employeeIds);
}

export function keepEdits(before: WorkDraft, latest: WorkDraft, draft: WorkDraft): WorkDraft {
  const employeesChanged = !sameEmployees(before.employeeIds, draft.employeeIds);
  return {
    volume: before.volume === draft.volume ? latest.volume : draft.volume,
    start: before.start === draft.start ? latest.start : draft.start,
    end: before.end === draft.end ? latest.end : draft.end,
    employeeIds: employeesChanged ? draft.employeeIds : latest.employeeIds,
    assignees: employeesChanged ? draft.assignees : latest.assignees,
  };
}

export function workInput(draft: WorkDraft, locale: string): ScheduleWorkInput | null {
  const volume = draft.volume.trim() ? parseNumber(draft.volume, locale) : null;
  if (draft.volume.trim() && (volume === null || volume < 0)) return null;
  if (draft.start && draft.end && draft.start > draft.end) return null;
  return { volume, start: draft.start || null, end: draft.end || null, employeeIds: draft.employeeIds };
}
