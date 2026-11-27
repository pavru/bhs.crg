import { useQuery, type QueryClient } from '@tanstack/react-query';
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
  state: ProgressState | 'no-accepted' | 'no-norms';
  link?: string | null;
  permission?: string | null;
  month?: string;
  labor?: {
    estimatedHours: number | null;
    actualHours: number | null;
    distributed: boolean;
    offEstimateHours: number | null;
    offEstimateWorks: { name: string | null; unit: string | null; volume: number }[];
    unallocatedHours: number | null;
    incomplete: boolean;
  };
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

const progressKey = ['construction-progress'] as const;
const siteKey = (constructionId: string) => [...progressKey, constructionId] as const;

export function invalidateConstructionProgress(client: QueryClient, constructionId?: string) {
  return client.invalidateQueries({ queryKey: constructionId ? siteKey(constructionId) : progressKey });
}

export function useConstructionProgress(constructionId: string, sectionId?: string, laborMonth?: string) {
  return useQuery({
    queryKey: [...siteKey(constructionId), sectionId ?? null, laborMonth ?? null],
    // Источники обновляются и в других вкладках, и на сервере. При возврате читаем текущий снимок.
    staleTime: 0,
    queryFn: ({ signal }) => apiClient.get<ConstructionProgress>(`/constructions/${constructionId}/progress`, {
      params: sectionId || laborMonth ? { ...(sectionId ? { section: sectionId } : {}), ...(laborMonth ? { laborMonth } : {}) } : undefined,
      signal,
    }).then(result => result.data),
  });
}
