import type { FactDiagnostics } from './progress';

export interface ScheduleSummary {
  id: string;
  constructionId: string;
  revisionId: string | null;
  name: string;
  version: string;
}

export interface ScheduleMark {
  code: string;
  text: string;
}

export interface ScheduleWarning extends ScheduleMark {
  workId: string;
  linkId: string | null;
  year: number | null;
}

export interface ScheduleAssignee {
  id: string;
  name: string;
  archived: boolean;
}

export interface ScheduleWorkInput {
  volume: number | null;
  start: string | null;
  end: string | null;
  employeeIds: string[];
}

export interface ScheduleWork extends ScheduleWorkInput {
  id: string;
  planItemId: string;
  workTypeId: string;
  unitId: string;
  sourceRowId: string | null;
  sectionId: string | null;
  name: string;
  manual: boolean;
  version: string;
  durationWorkingDays: number | null;
  unitName: string | null;
  assignees: ScheduleAssignee[];
  warnings?: ScheduleWarning[];
  /** Факт приходит из полного чтения графика. Ответы записи могут его не содержать. */
  fact?: ScheduleFact | null;
  productivity?: ScheduleProductivity;
}

export interface ProductivitySample { reports: number; constructions: number }

export interface ProductivityMonth {
  month: string;
  productivity: { state: 'ok' | 'too-little-data'; volumePerHour?: number; sample: ProductivitySample };
}

export type ScheduleProductivity =
  | { state: 'ok' | 'too-little-data'; months: ProductivityMonth[]; minSample: ProductivitySample }
  | { state: 'hidden' | 'not-connected' | 'not-available'; permission?: string; text?: string };

export type ScheduleFact =
  | { state: 'ok' | 'partial'; volume: number | null }
  | { state: 'hidden' | 'not-connected'; permission?: string | null; text?: string | null };

export interface ScheduleLink {
  id: string;
  predecessorId: string;
  successorId: string;
  kind: string;
  lag: number;
  version: string;
  predecessor?: ScheduleWorkRef;
  successor?: ScheduleWorkRef;
}

export interface ScheduleLinkInput {
  predecessorId: string;
  successorId: string;
  kind: 'fs';
  lag: number;
}

interface ScheduleSkipped { unmapped: number }

export interface Schedule extends ScheduleSummary {
  skipped: ScheduleSkipped;
  marks: ScheduleMark[];
  works: ScheduleWork[];
  links: ScheduleLink[];
  warnings: ScheduleWarning[];
  factDiagnostics?: FactDiagnostics | null;
}

export interface ScheduleCreate {
  constructionId: string;
  revisionId: string | null;
  creationKey: string;
  name: string;
}

export interface ScheduleWorkRef {
  id: string;
  name: string;
  scheduleId: string;
  scheduleName: string;
  sectionId: string | null;
}

interface ScheduleDifference {
  workId: string | null;
  rowId: string;
  sectionId: string | null;
  name: string;
  volume: number | null;
}

interface ScheduleVolumeChange extends Omit<ScheduleDifference, 'workId' | 'volume'> {
  workId: string;
  before: number | null;
  after: number | null;
}

interface ScheduleRemovedLink {
  id: string;
  predecessor: ScheduleWorkRef;
  successor: ScheduleWorkRef;
  kind: string;
  lag: number;
}

export interface ScheduleRebuildPreview {
  version: string;
  fingerprint: string;
  added: ScheduleDifference[];
  removed: ScheduleDifference[];
  changed: ScheduleVolumeChange[];
  skipped: ScheduleSkipped;
  removedLinks: ScheduleRemovedLink[];
}
