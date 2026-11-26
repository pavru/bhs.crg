import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

export interface FactDiagnostics {
  scope: { constructionId: string; from: string | null; to: string | null };
  awaitingAcceptance: number;
  excludedCrewGroups: number;
  crewExclusions: { reason: 'NotApproved' | 'Stale' | 'AwaitingAcceptance' | 'InvalidKey' | 'MultipleKeys'; groups: number }[];
  withoutPlanItem: number;
  unsubmitted: number;
}

type ProgressState = 'ok' | 'partial' | 'hidden' | 'not-available' | 'not-connected';

export interface ProgressColumn {
  code: string;
  title: string;
  kind: 'Volume' | 'Dates';
  state: Exclude<ProgressState, 'not-connected'>;
  text?: string | null;
  permission?: string | null;
}

export interface ProgressValue {
  volume: number | null;
  start: string | null;
  end: string | null;
}

export interface ProgressRow {
  workTypeId: string;
  unitId: string;
  workTypeName: string | null;
  unitName: string | null;
  cells: Record<string, ProgressValue | undefined>;
}

export interface ProgressStrip {
  code: string;
  text: string;
  state: ProgressState;
  link?: string | null;
  permission?: string | null;
}

export type ProgressCosts =
  | { state: 'ok'; totals: { allocated: number; paid: number; linkedToWorks: { state: 'not-tracked'; reason: string } } }
  | { state: 'hidden' | 'not-connected' | 'not-available'; text?: string | null; permission?: string | null };

export interface ConstructionProgress {
  constructionId: string;
  sectionId: string | null;
  columns: ProgressColumn[];
  rows: ProgressRow[];
  strips: ProgressStrip[];
  costs: ProgressCosts;
}

export function useConstructionProgress(constructionId: string, sectionId?: string) {
  return useQuery({
    queryKey: ['construction-progress', constructionId, sectionId ?? null],
    queryFn: ({ signal }) => apiClient.get<ConstructionProgress>(`/constructions/${constructionId}/progress`, {
      params: sectionId ? { section: sectionId } : undefined, signal,
    }).then(result => result.data),
  });
}
